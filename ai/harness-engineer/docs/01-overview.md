# Harness Engineer — Overview

> Harness engineering is the discipline of building the software scaffolding around an LLM — the loop, tools, memory, guardrails, and observability — that turns a model into an agent.

## The 30-second version
An LLM on its own is a text-in, text-out function. It does not remember things, run code, call APIs, retry on failure, or know when to stop. **The harness is everything around the model that makes those things happen.** A harness engineer designs and maintains that surrounding system: the orchestration loop, tool execution, context assembly, state and memory, validation, error handling, and tracing. The shorthand that crystallized in 2026 is `Agent = Model + Harness`, and the industry quietly accepted that a decent model with a great harness reliably beats a great model with a bad harness.

## The mental model
Picture a fighter pilot in a cockpit. The pilot is brilliant, fast, and trained — but on their own they are just a person in a seat. What makes the aircraft fly is the cockpit around them: instruments that tell them what is happening, controls that translate intent into action, autopilot that handles routine work, alarms that catch mistakes, and a black box that records everything for later review.

The LLM is the pilot. The harness is the cockpit.

Or, if you prefer a CPU analogy: an LLM is a CPU — a powerful but stateless compute unit. The harness is the operating system around it. The OS schedules work, manages memory, opens files, handles interrupts, and recovers from crashes. Nobody writes "the CPU did the work" — the OS is doing 90% of what you call "running a program." Same here. In a production agent, the LLM call is a small slice of the system; the harness is the rest.

## What it is NOT
- **Not the model.** Swapping GPT for Claude does not rewrite your harness, and a better model does not fix a broken loop.
- **Not prompt engineering.** Prompts are *one input* into a harness; harness engineering owns the whole runtime.
- **Not a framework.** LangChain, LlamaIndex, etc. are starter kits. Your harness is the actual production system, framework-based or not.
- **Not RAG.** Retrieval is one component the harness orchestrates, not the discipline itself.
- **Not "AI engineering" in general.** AI engineering is the umbrella; harness engineering is the runtime-systems specialty inside it.

## When you would reach for it
- You are putting an agent in front of real users and "it works on the demo" is not enough.
- Your agent has to use tools, call APIs, or modify state in the real world.
- You need traces, retries, timeouts, and budgets — the boring production qualities.
- You want to swap models without rewriting the application.
- Multi-step workflows are starting to drift, hallucinate tool calls, or silently fail mid-run.

## When you would NOT reach for it
- A single-turn chatbot with no tools and no memory — a prompt is enough.
- A one-off script or notebook experiment.
- A pure classification or embedding task where there is no loop and no agency.

## Key vocabulary (just enough to keep reading)
- **Agent loop / ReAct loop** — the Thought → Action → Observation cycle the harness runs.
- **Tool / function call** — a typed capability (search, SQL, shell) the model can invoke.
- **Context window** — the bounded text the model sees on each call; the harness decides what goes in.
- **Short-term memory** — the working scratchpad inside one run.
- **Long-term memory** — persistent store (often vector-indexed) across runs.
- **Guardrail** — a validator or policy that gates inputs, outputs, or tool calls.
- **Observability / trace** — structured logs of every step the agent took and why.
- **Stopping condition** — the rule that ends the loop (done, budget hit, error, hand-off).
- **Control plane** — the policy and routing layer above individual agent runs.

## What's next
- `02-deep-dive.md` — What / Where / When / How / Why, with the six core harness components and a reference architecture.
- `03-practice.md` — real-world patterns and anti-patterns: context drift, tool sprawl, infinite loops, eval gates, and what production teams actually do.
- `code/mvp.py` — a minimal hand-rolled harness that wraps an LLM call into a working tool-using agent loop.
