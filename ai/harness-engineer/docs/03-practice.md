# Harness Engineer — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

The harness is the layer you end up writing once a notebook prototype graduates to a service real users hit. In a coding-agent product (Claude Code, Cursor, Codex CLI, Devin) it owns the shell, the file IO, the diff application, and the long-running session that survives a crashed worker. In a customer-support or ops agent inside a SaaS backend it sits between the chat gateway and the ticket/Slack/Stripe tools, owning permissions and audit trails. In an internal data agent it is the thing that decides whether the model gets to run a `DELETE` against the warehouse.

The pattern is consistent: there is one process per agent run, the LLM call is a small slice of a turn, and 70–80% of the code is everything else — retries, schemas, traces, budgets, checkpoints. If you find yourself reasoning about "why did the agent loop forever last Tuesday at 3am", you are now a harness engineer whether your title says so or not.

## Best practices

### 1. Own the control loop explicitly
**Do:** Write the `while`/`for` loop and the stopping conditions in your own code. Even if you use LangGraph or the Agents SDK, the routing of "tool call vs. final answer vs. handoff" is your code, not framework magic.
**Why:** When a run misbehaves at 2am, you need to read one function and know exactly what happened. Frameworks change minor versions and silently shift loop semantics; your on-call doesn't.
**Avoid:** `agent.run(task)` as a black box where step counts, retries, and budgets are configured via YAML.

### 2. Treat tools as a versioned API surface
**Do:** Each tool has a stable name, a JSON schema, a docstring written *for the model*, a typed return shape, and a version. Add new tools; never silently change argument shapes.
**Why:** Tool-call schema drift is one of the most-cited production failure modes — the n8n February 2026 post-mortem traces a whole outage to it. Once the model has cached behavior around a tool, changing its shape produces malformed-but-plausible calls, not errors.
**Avoid:** Renaming `search_docs(query)` to `search_docs(q, top_k)` in place because "the model will figure it out."

### 3. Assemble context in named, replaceable slices
**Do:** Build the prompt from explicit slots — `system`, `tool_catalog`, `memory_slice`, `scratchpad`, `user_msg` — each produced by its own function and individually testable. Reserve a fixed output budget; compact when the input crosses a threshold.
**Why:** When recall drops, you need to A/B *one slice* (e.g., swap retrieval) without rebuilding the prompt. MemU's 2026 measurement of ~2% context retention loss per step makes the slice that owns compaction the highest-leverage thing to tune.
**Avoid:** A single 600-line f-string that nobody dares to refactor.

### 4. Deterministic where possible, model where necessary
**Do:** If a step can be a `regex`, a SQL query, or a typed function, make it that. Reserve the LLM for steps that genuinely require judgment.
**Why:** Every model call costs latency, money, and a chance of hallucination. Production harnesses for coding agents push parsing, file globbing, and diff application out of the model entirely.
**Avoid:** Asking the model to "extract the JSON" from a string you already control.

### 5. Checkpoint every state transition
**Do:** Persist `(thread_id, step, state)` after each tool call and each model response. Use a real store (Postgres, Redis Streams, LangGraph checkpointer, Temporal) — not in-process memory.
**Why:** Worker pods get rescheduled. A 40-step run that loses state on minute 12 is more expensive than the entire infra bill for the day. Checkpointing also enables human-in-the-loop pauses for free.
**Avoid:** Keeping the conversation in a Python list on a single worker.

### 6. Small reversible tools beat big "do anything" tools
**Do:** Prefer `read_file`, `write_file`, `run_tests`, `apply_patch` over `do_engineering_task(description)`. Reversible side effects (with idempotency keys) over irreversible ones.
**Why:** The Replit July 2025 incident — agent ran `DROP DATABASE` against prod despite a freeze — is the canonical case for narrow tools with explicit blast radius. Big tools also confuse the model: it picks the broad one to "be safe."
**Avoid:** A single `execute(command)` tool with shell access and no allowlist.

### 7. Validate tool arguments before invocation
**Do:** JSON-schema-validate every tool call, coerce types, and reject on mismatch with a structured error the model can read. Same for outputs that claim to be JSON.
**Why:** Individual tool calls fail 3–15% in production (per Atlan's 2026 anti-pattern report), and most failures are malformed args, not bad logic. A validator turns a silent corruption into a recoverable loop step.
**Avoid:** `json.loads(model_output)` with a bare `try/except`.

### 8. Idempotency keys on every state-mutating tool
**Do:** The harness passes `request_id = f"{thread_id}:{step}:{call_id}"` to every write tool. Tools dedupe on it.
**Why:** Retries are not optional in distributed systems. Without idempotency, an exponential backoff sends two payments. This is the single most common money-losing bug in agent products.
**Avoid:** "We'll add idempotency later" — by the time you need it, you have a customer-facing incident.

### 9. Budgets are hard caps, not metrics
**Do:** Enforce `max_steps`, `max_tokens`, `max_wall_clock`, `max_dollars_per_task` inside the loop. When exceeded, transition to a graceful-stop branch that produces a partial answer plus a trace link.
**Why:** AutoGPT-style runaway loops still happen in 2026; they just look like a Stripe bill instead of a news story. Budgets bound the worst case.
**Avoid:** Logging token spend to Datadog and trusting yourself to notice.

### 10. Evals are the harness regression suite, not the model's
**Do:** Maintain a corpus of golden traces (input + expected tool-call sequence + final state). Replay nightly. Gate prompt and tool changes on diff against this corpus, not on vibes.
**Why:** The model is a moving target you don't control; your harness is yours. Most regressions you'll ship are tool-schema changes and prompt edits, both eval-catchable.
**Avoid:** "It worked on the demo task" as a release criterion.

### 11. Trace everything with span IDs the model never sees
**Do:** One OTEL span per LLM call, per tool call, per decision. Persist inputs and outputs. Generate a `trace_id` at ingress and propagate it. *Never* paste trace data into the model's context.
**Why:** A new engineer should be able to open a trace and replay any production run locally in under five minutes. Mixing observability data into the conversation pollutes the context and burns tokens.
**Avoid:** `print()` statements and a hope.

### 12. Adopt MCP instead of inventing a tool protocol
**Do:** Expose tools over MCP so the same tool server works across Claude, GPT, Gemini harnesses and across your own products.
**Why:** Tool sprawl is the dominant ongoing cost in a maturing agent codebase. MCP standardizes the contract the same way LSP did for editors. Rebuilding a worse version internally pays nothing back.
**Avoid:** A bespoke `tools.yaml` format that only your harness can read.

## Anti-patterns to recognize

- **Framework owns the loop.** Code reads `crew.kickoff()` and the actual control flow lives in a vendor's decorator stack. Failures become unsearchable. *Better:* keep the loop and stopping conditions in your repo; let the framework own state and traces if you want, but not the routing.
- **Unbounded scratchpad.** Every turn appends, nothing compacts, eventually a 200k-token context costs $4 per turn and the model forgets the original task. *Better:* a compaction policy that triggers at a fraction of the window, with templated state (decisions, open questions, facts) rather than free-form summarization.
- **Tool sprawl in one registry.** 50 tools all advertised on every call. Token bloat plus model confusion — accuracy drops sharply past ~15 tools in published benchmarks. *Better:* progressive disclosure — subagents or phase-specific tool subsets, à la Claude Code's per-subagent `tools` list.
- **Mutating tools without idempotency.** Retry sends two emails, double-charges a card, opens two tickets. *Better:* request IDs flowed by the harness, dedupe at the tool boundary.
- **"It worked once" as a test.** No replayable trace, no golden corpus, no regression. The next prompt edit silently breaks production. *Better:* a trace replayer plus 20–100 golden cases gated in CI.
- **Observability inside the conversation.** Stack traces and span IDs pasted into tool results. The model latches onto noise, costs balloon, prompt injection surface grows. *Better:* structured logs out-of-band; the model sees only what it needs to reason.
- **Trusting model JSON.** `json.loads` without schema validation, no repair loop. *Better:* JSON-schema validate; on failure, return a structured error to the model and let it self-correct one time before failing the turn.
- **Reinventing MCP.** Custom tool-discovery protocol because "ours is simpler." Six months later you have three half-built versions and no shared tools across products. *Better:* adopt MCP and contribute upstream.

## Real-world usage patterns

- **Coding agent at IDE scale (Cursor, Windsurf).** Editor plugin + cloud harness. Codebase indexing lives behind a tool (semantic search, symbol lookup) rather than getting stuffed into context. Speculative tool calls are pre-warmed against the KV cache so apply-edit feels instant. *Lesson:* treat your index as a service, not as context — context is the most expensive memory tier you own.
- **Long-running autonomous coder (Devin, SWE-agent variants).** Explicit Plan → Execute → Verify phases. Planner runs on a strong model with a small tool set; executor uses a cheaper model with a wide tool set; verifier runs the test suite and gates progress. *Lesson:* splitting planning from execution is what keeps a 200-step run from drifting; the verifier is the only honest stopping condition.
- **Agentic coding CLI (Claude Code, Codex CLI).** Subagents with their own context window and tool allowlist (`Read, Grep, Glob` for a reviewer, full write access for an implementer). A `/compact` command surfaces compaction as a user-visible primitive instead of hiding it. *Lesson:* tool permissioning belongs at the subagent boundary, enforced in code — not in the system prompt — because prompts are advisory and allowlists are not.
- **Customer-support agent in a B2B SaaS backend.** LangGraph state machine, ~8 tools (search KB, lookup user, create ticket, refund within $X), human-in-the-loop approval for refunds above a threshold, full OTEL tracing to Langfuse. *Lesson:* the moment a tool spends money, the harness needs an approval queue and an audit log — that turns the agent from a demo into a regulated workflow.
- **Internal data analyst agent.** SQL tool against a read-replica only, with a query allowlist and a row-count cap. The harness rewrites generated SQL through a validator before execution. *Lesson:* the safest write tool is no write tool; if the read-only version solves the task, ship that and stop.

## Operational checklist

- **Monitoring:** are you emitting per-turn metrics — success rate, p95 step count, p95 tokens, p95 wall-clock, tool-call failure rate per tool, cost per *successful* task?
- **Tracing:** can a new engineer pull any production `trace_id` and replay it locally in under five minutes?
- **Failure handling:** is there a tested path for "LLM provider returns 503 for 90 seconds" and for "tool times out mid-turn"? Does the agent resume or fail cleanly?
- **Idempotency:** have you forced a duplicate `request_id` against every state-mutating tool and confirmed no double side-effects?
- **Loop safety:** are `max_steps`, `max_tokens`, `max_wall_clock`, and `max_dollars_per_task` enforced *inside* the loop, not just logged?
- **Security:** does each tool have a capability scope tied to the caller's identity? Is tool output treated as untrusted text (prompt-injection screened)?
- **Cost:** what is the cost-per-success on your top 5 task types? Which tool's tokens dominate?
- **Evals:** is the golden-trace suite wired into CI and blocking on regression?
- **Onboarding:** can a new engineer point at one file and say "this is the control loop"? Can they add a tool without editing more than two files?
- **Documentation:** is the tool catalog auto-generated from schemas so it cannot drift from the code?

## How this topic typically evolves in a codebase

Most teams start with a single Python file: one LLM call, a `while` loop, two tools, an in-memory message list. This is correct — premature harness platforming is its own anti-pattern. The first painful migration usually arrives when sessions need to survive a deploy: the in-memory state becomes a Redis/Postgres checkpoint, and the worker becomes a queue consumer rather than an HTTP handler. Right around this point teams adopt OTEL tracing, because without it the first production bug is unsolvable.

The second migration is tool federation. The tool registry was a Python dict; it becomes an MCP server (often several), once a second product or a second team wants the same `search_kb`. The third — and the one most teams underestimate — is splitting the monolithic agent into subagents with scoped tool allowlists. This happens not because anyone wanted multi-agent architecture, but because the tool catalog hit ~20 items and accuracy started dropping. At that point the harness looks less like a script and more like a small distributed system: a control plane, durable workers, a tool plane, an eval plane, an observability plane. The codebase that started as `agent.py` ends up as a service with its own SLO.

The lesson worth internalizing early: every successful harness ends here. Build the first version simply, but pick abstractions (named context slices, idempotent tools, explicit budgets, schema-validated calls) that survive the journey.

## Further reading

- [Harness engineering: leveraging Codex in an agent-first world](https://openai.com/index/harness-engineering/) — the OpenAI post that named the discipline. Read for the framing of `Agent = Model + Harness` and the loop primitives.
- [Harness engineering for coding agent users (Martin Fowler / Birgitta Böckeler)](https://martinfowler.com/articles/harness-engineering.html) — the canonical writeup outside the labs. Strongest treatment of how harness choices show up in everyday coding-agent UX.
- [Inside Claude Code — The Architecture Behind Tools, Memory, Hooks, and MCP](https://www.penligent.ai/hackinglabs/inside-claude-code-the-architecture-behind-tools-memory-hooks-and-mcp/) — concrete reference architecture: five-layer compaction, permission classifier, subagent allowlists. Best single source on what "production harness" actually looks like.
- [AI Agent Harness Failures: 13 Anti-Patterns and Root Causes (Atlan, 2026)](https://atlan.com/know/agent-harness-failures-anti-patterns/) — failure taxonomy with named post-mortems (Replit `DROP DATABASE`, n8n schema drift). Read before designing tool permissions.
- [Awesome Harness Engineering](https://github.com/ai-boost/awesome-harness-engineering) — curated index of tools, patterns, evals, MCP servers, observability backends. Use as a starting catalog when picking libraries.
- [Model Context Protocol specification](https://modelcontextprotocol.io) — the tool protocol itself. Read the spec before writing a custom one; you almost certainly should not write a custom one.
