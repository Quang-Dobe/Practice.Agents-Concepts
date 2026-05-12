# Harness Engineer — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition
A **harness** is the deterministic software system that wraps a non-deterministic language model and turns it into an agent. Formally: given a model `M: prompt -> completion`, the harness is the program `H` such that `Agent(task) = H(M, task)`, where `H` owns the control loop, tool invocation, context construction, persistence, validation, and observability. The model is a single stateless function call inside `H`; everything else — scheduling, memory, IO, recovery, policy — is harness code. A **harness engineer** is the person who designs, builds, and operates `H`. The role is most accurately described as *platform engineering applied to LLM runtimes*: same posture as SRE/platform work (budgets, traces, retries, idempotency), different substrate (a probabilistic component in the hot path).

### The core building blocks
Every non-trivial harness contains roughly seven subsystems. Most production failures map to a missing or weak one.

- **Orchestration / control loop.** The Thought→Action→Observation cycle, often a ReAct variant or a Plan‑Execute‑Verify (PEV) phase-gate. Owns step counting, stopping conditions, and the decision of whether to call the model again, call a tool, hand off to a subagent, or terminate.
- **Tool registry and invocation.** A typed catalog of capabilities (search, SQL, shell, HTTP) exposed to the model via JSON schema. Owns schema validation, argument coercion, timeouts, sandbox boundaries, and result formatting. Increasingly federated through the **Model Context Protocol (MCP)**, which standardizes the client↔tool-server contract so the same tool works across Claude, GPT, and Gemini harnesses.
- **Context window management.** The component that decides what tokens go into each model call. Three sub-layers: a *short-term scratchpad* (current turn history), a *long-term store* (vector index, KV store, or files), and a *compaction/summarization* policy that triggers near the token budget — Anthropic and Cursor both report compaction reducing context by 60–80% on long sessions.
- **State persistence and resumption.** Durable checkpoints between turns: thread state, intermediate results, pending tool calls. Enables crash recovery, human-in-the-loop pauses, and resuming a multi-hour run on another worker. LangGraph's `Checkpointer` and Anthropic's git-backed `claude-progress.txt` pattern are two reference implementations.
- **Validation and guardrails.** Pre-call (input policy, PII redaction, prompt-injection screening) and post-call (schema validation on tool args, output classifiers, refusal detection). Layered as *preventive* (block before execution) plus *corrective* (detect and self-correct in the loop).
- **Observability and tracing.** Structured spans for every LLM call, tool call, and decision, exported via OpenTelemetry GenAI semantic conventions (the OTEL working-group spec stabilized in 2025) to backends like Langfuse, Arize Phoenix, Braintrust, or LangSmith. Without this, evals are guesswork.
- **Safety and permissions.** Capability scoping per tool, per user, per environment. Approvals for state-mutating actions, rate limits, and budget enforcement (tokens/$/wall-clock). Anything that touches money or production state lives behind this layer.

### How it relates to the broader landscape
Harness engineering sits inside **AI engineering** as the runtime-systems specialization, distinct from *prompt engineering* (input design), *model engineering* (training/fine-tuning), and *RAG engineering* (a retrieval component, not a runtime). Sibling disciplines outside AI: backend platform engineering (it borrows control planes, idempotency, traces), workflow orchestration (Temporal, Airflow — same lifecycle problems, deterministic workers), and OS kernels (the "LLM is a CPU, harness is the OS" analogy is structurally accurate, not just rhetorical).

## Where

### Where it runs / lives in the stack
The harness sits **between the API gateway and the LLM provider**. A typical request path: client → gateway/auth → harness process → (LLM API, tool calls, vector DB, state store) → response. Two deployment shapes dominate:

- **In-process library** inside a backend service — fine for low-concurrency, single-tenant agents. LangChain/LlamaIndex apps usually start here.
- **Harness-as-a-service** — a dedicated control plane (its own deploy, queue, worker pool, durable checkpointer) that other services call. This is what Claude Code, Cursor, Devin, and Replit Agent run internally; it is what LangGraph Platform, OpenAI's AgentKit, and CrewAI Enterprise sell.

The harness is a **stateful long-running compute** boundary. Treat it like a job scheduler with an unreliable worker, not like a request/response API.

### Where you typically encounter it
- **Claude Code** and **Codex** — agentic coding harnesses with shell, file IO, and git tools.
- **Cursor** and **Windsurf** — IDE-embedded harnesses, heavy on speculative tool calls and KV-cache reuse.
- **Devin**, **Factory**, **Cognition** — long-running autonomous coding agents.
- **GitHub Copilot Workspace** — issue-to-PR harness with planning and verification phases.
- **Customer-support and ops agents** at most SaaS vendors — typically built on LangGraph or a homegrown loop.

### Ecosystem and tooling
- **For orchestration:** LangGraph (graph state machine, the production default in early 2026), CrewAI (role-based crews, fast prototyping), Microsoft AutoGen / AG2 (conversational multi-agent), OpenAI Agents SDK, Pydantic AI.
- **For tools/integration:** MCP (the de facto tool protocol since late 2025), OpenAI function calling, Anthropic tool use, native function calling on Gemini.
- **For memory:** pgvector, Qdrant, Weaviate, LanceDB; Mem0 and Zep for opinionated long-term memory layers.
- **For observability and evals:** Langfuse, Arize Phoenix, LangSmith, Braintrust, Helicone, OpenLLMetry (OTEL instrumentation).
- **For state and durability:** LangGraph Checkpointer, Temporal (durable workflows), Inngest, Redis streams.
- **For safety:** NeMo Guardrails, Llama Guard, Lakera Guard, Pillar.

## When

### When the topic emerged and why
The phrase "harness engineering" entered common usage in 2025 after OpenAI's "Harness engineering: leveraging Codex in an agent-first world" post and crystallized in early 2026 with Anthropic's "Effective harnesses for long-running agents" and Martin Fowler's writeup citing Birgitta Böckeler's `Agent = Model + Harness` framing. The motivating observation: as frontier models converged in raw capability (GPT-5 class, Claude 4.x, Gemini 2.5), the variance in agent quality stopped tracking model choice and started tracking the surrounding system. Before 2024, "agent" mostly meant a single ReAct loop in a notebook; the field had AutoGPT and BabyAGI, both of which famously failed in production for harness reasons (no stop conditions, no state, no eval). The name became necessary once teams realized they were hiring people specifically to build that surrounding system.

### When to use it in a project
Reach for real harness engineering when:
- The task is **multi-turn** and the model needs more than one tool call to finish.
- Tools **mutate state** (writes to a DB, sends email, calls a payment API) — you now need idempotency, approvals, and traces.
- You have a **cost or latency SLO** (per-task token budget, p95 wall-clock) you must enforce, not just measure.
- Sessions are **long-running or resumable** (minutes to hours; humans approve midway).
- You want **eval-driven iteration** — you cannot improve what you cannot replay.
- You expect to **swap models** without rewriting application logic.

### When NOT to use it
Avoid building a harness when:
- A single-turn prompt with no tools solves the task — a chat completion call is enough.
- The workload is **batch classification or embedding** — no loop, no agency, no harness needed.
- You are still figuring out whether the task is feasible at all — prototype in a notebook first.
- Team size is one engineer and the use case is internal — a thin LangChain script is the right level for weeks, maybe months.

## How

### How it works under the hood
One full agent turn, end to end, in a production harness:

1. **Ingress.** The gateway validates auth, applies rate limits, attaches a `trace_id`, and enqueues a task. The harness worker picks it up and loads any prior checkpoint for this thread.
2. **Context assembly.** The context builder pulls: system prompt, tool schemas (filtered by user permissions), retrieved long-term memory, recent scratchpad, and the current user message. It tokenizes, checks against the model's window minus a reserved output budget, and triggers compaction if over threshold.
3. **Model call.** The harness calls the LLM with `tools=[...]`. It streams tokens for UX but only commits the result after the full message arrives. Transient `5xx`/`429` errors are retried with exponential backoff and jitter; persistent failures fall through to a configured fallback model or fail the turn cleanly.
4. **Decision.** Parse the response. If it is a final answer, validate it (schema, refusal classifier) and return. If it contains tool calls, route each to the registry.
5. **Tool invocation.** Each call is validated against its JSON schema, scoped to the caller's permissions, executed inside a timeout and sandbox, and logged as its own span. Tools are designed **idempotent** — a `request_id` from the harness lets retried calls coalesce instead of duplicating side effects.
6. **State update.** Append the tool results to the scratchpad, persist the new checkpoint, decrement the token/step/$ budget. If any budget is exhausted, transition to a graceful-stop branch.
7. **Loop or terminate.** If a stopping condition holds (final answer, budget exhausted, human approval required, max steps), exit. Otherwise return to step 2.

A minimal Python sketch — illustrative, not production:

```python
def run_turn(thread_id, user_msg, budget):
    state = checkpointer.load(thread_id)
    state.append({"role": "user", "content": user_msg})

    for step in range(budget.max_steps):
        ctx = context_builder.assemble(state, budget.tokens_left)
        resp = call_model_with_retry(ctx, tools=registry.schemas())
        state.append(resp.message)

        if not resp.tool_calls:
            guardrails.validate_output(resp.message)
            checkpointer.save(thread_id, state)
            return resp.message

        for call in resp.tool_calls:
            guardrails.validate_call(call)
            result = registry.invoke(
                call.name, call.args,
                request_id=f"{thread_id}:{step}:{call.id}",
                timeout=budget.tool_timeout,
            )
            state.append({"role": "tool", "id": call.id, "content": result})

        budget.spend(resp.usage)
        checkpointer.save(thread_id, state)
        if budget.exhausted():
            return graceful_stop(state)

    return graceful_stop(state)
```

**Evals close the loop.** Every trace is replayable. A regression suite of "golden traces" reruns nightly against the current model + harness; metrics include task success, step count, token spend, and tool-call validity. Prompt or tool changes ship only when eval deltas are positive.

### Key trade-offs

| Design choice | Gained | Given up |
|---|---|---|
| In-process loop vs. durable workflow engine | Simplicity, low latency | No crash recovery, no resumability |
| Big single agent vs. multi-subagent | Easier to reason about | One context window, less parallelism |
| Strict JSON tool schemas vs. free-form | Determinism, validation | Model expressiveness on edge cases |
| Aggressive compaction vs. raw history | Fits long sessions | Lossy — facts get dropped (measured ~60% fact retention under naive summarization) |
| MCP-federated tools vs. in-tree | Reuse, vendor portability | Extra hop, schema drift across servers |
| Framework (LangGraph) vs. hand-rolled | Free observability, checkpointing | Coupling to the framework's state model |

### Common failure modes
- **Infinite loop.** Model keeps calling the same tool. *Cause:* no step cap or no progress check on observations.
- **Context overflow.** Token limit hit mid-run. *Cause:* unbounded scratchpad, no compaction policy.
- **Sycophantic confirmation.** Model claims "done" before the work is done. *Cause:* no verification phase or external success criterion.
- **Tool sprawl.** 50+ tools confuse the model. *Cause:* no progressive disclosure; everything in the system prompt.
- **Silent fact destruction during compaction.** Summary drops the critical constraint. *Cause:* unstructured "summarize this" instead of templated state.
- **Double-execution.** Retry sends two payments. *Cause:* non-idempotent tools, no `request_id` deduplication.
- **Prompt injection via tool output.** Web page contains "ignore previous instructions". *Cause:* tool results treated as trusted text.
- **Trace blindness.** Cannot reproduce a bug. *Cause:* logs without span IDs or input/output payloads.

## Why

### Why it exists
Models are stateless probability distributions over tokens. Production software needs state, retries, transactions, audits, budgets, and approvals. The harness is the boundary that converts a probabilistic component into a system you can run, debug, bill, and trust. It exists for the same reason an OS exists around a CPU: the compute element is not where most of the engineering lives.

### Why it looks the way it does
Two non-obvious design choices recur:

- **Why a loop and not a graph?** Pure DAGs (Airflow-style) cannot express "the agent decides what to do next." Pure free-form loops (AutoGPT) cannot be debugged. The industry settled on *constrained graphs with loops* (LangGraph) or *typed phase machines* (PEV) because they preserve agent autonomy inside a structure that humans can read and bound.
- **Why standardize tools (MCP) instead of letting each app define its own?** Because tool sprawl is the dominant cost, and every team was reinventing the same shell/filesystem/HTTP tool. A protocol makes tools a *market* rather than a *moat* — same logic as LSP for editors. The trade-off is a network hop and a schema-versioning problem, both judged worth it.

### Why it matters now
As of 2026, the strongest empirical claim in the field is that **harness quality dominates model choice** for most agent tasks. Anthropic's three-agent harness post, Cursor's published architecture, and Augment Code's coding-agent guides all make this case with numbers. The labs (Anthropic, OpenAI, Google, Microsoft) now agree the harness is the product — they only disagree on pricing. For a working engineer that means: the leverage is in the harness, the role is hiring at a 10–20% premium over equivalent backend roles, and the discipline maps cleanly onto skills any solid platform engineer already has.

## Open questions / things to verify in practice
- What is the actual step-count distribution of your task? If p95 is 3 steps, you do not need durable checkpoints; if it is 50, you absolutely do.
- Where does compaction lose information on your real traces? Run a "constraint recall" eval before and after compaction.
- Are your tools genuinely idempotent? Force a duplicate `request_id` and confirm no double side-effects.
- Does your stopping condition fire on "model believes it is done" or on "external verifier says it is done"? Only the second is safe.
- What is the cost per successful task, not per call? Token cost without success rate is meaningless.
- Can a new engineer replay any production trace locally in under five minutes? If not, your observability is theatre.
