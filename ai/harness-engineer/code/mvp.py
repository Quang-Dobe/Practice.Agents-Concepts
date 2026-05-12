"""
Harness Engineer MVP
====================

The smallest possible end-to-end demo of an LLM *harness*: the deterministic
software loop that wraps a non-deterministic model and turns it into an agent.

What this file proves, from the deep-dive doc:
  - The model is a single stateless function call inside the harness; the
    harness owns the control loop, tool registry, context, validation,
    budgets, and observability.
  - Swapping the model is a one-line change: `FakeLLM` here, an SDK client
    later. The harness above it does not change.
  - The loop terminates on (a) a final answer, (b) a hard step cap, or
    (c) a token-budget cap -- never on "the model felt done".

We use a `FakeLLM` with a scripted sequence so the demo runs offline with
zero installs. Everything that is *not* the model is real harness code.

Run:    python mvp.py
"""

from __future__ import annotations

import json
import time
from dataclasses import dataclass, field
from typing import Any, Callable


# ---------------------------------------------------------------------------
# 1. Tool registry
# ---------------------------------------------------------------------------
# Tools are pure Python functions paired with a JSON schema. The schema is
# the contract the model sees; the function is what the harness actually
# executes. We validate args against the schema *before* invocation so a
# malformed call becomes a recoverable loop step, not a crash.


@dataclass
class Tool:
    name: str
    description: str           # written for the model, not the human
    schema: dict[str, Any]     # minimal JSON-schema-ish: {"arg": "type"}
    fn: Callable[..., Any]


TOOLS: dict[str, Tool] = {}


def register(name: str, description: str, schema: dict[str, Any]):
    """Decorator that puts a function into the global tool registry."""
    def wrap(fn: Callable[..., Any]) -> Callable[..., Any]:
        TOOLS[name] = Tool(name=name, description=description, schema=schema, fn=fn)
        return fn
    return wrap


@register("add", "Return a + b.", {"a": "number", "b": "number"})
def _add(a: float, b: float) -> float:
    return a + b


@register("multiply", "Return a * b.", {"a": "number", "b": "number"})
def _multiply(a: float, b: float) -> float:
    return a * b


@register("now", "Return current wall-clock time as a string.", {})
def _now() -> str:
    return time.strftime("%Y-%m-%d %H:%M:%S")


def _validate_args(tool: Tool, args: dict[str, Any]) -> None:
    """Toy schema check: required keys present, numeric types coerce."""
    for key, ty in tool.schema.items():
        if key not in args:
            raise ValueError(f"tool {tool.name!r} missing arg {key!r}")
        if ty == "number" and not isinstance(args[key], (int, float)):
            raise ValueError(f"tool {tool.name!r} arg {key!r} not numeric: {args[key]!r}")
    extra = set(args) - set(tool.schema)
    if extra:
        raise ValueError(f"tool {tool.name!r} got unexpected args: {sorted(extra)}")


def invoke_tool(name: str, args: dict[str, Any]) -> Any:
    """Validate and dispatch. The harness owns this -- the model does not."""
    if name not in TOOLS:
        raise ValueError(f"unknown tool: {name!r}")
    tool = TOOLS[name]
    _validate_args(tool, args)
    return tool.fn(**args)


# ---------------------------------------------------------------------------
# 2. Fake LLM
# ---------------------------------------------------------------------------
# A real model returns either a "tool_call" message or a "final" message.
# We script the same shape here. The harness code below treats this object
# identically to a real provider response -- that is the whole point.


@dataclass
class LLMResponse:
    kind: str                       # "tool_call" or "final"
    tool: str | None = None
    args: dict[str, Any] | None = None
    content: str | None = None      # final answer text
    tokens_used: int = 0            # pretend-cost the harness must budget


class FakeLLM:
    """
    Deterministic, scripted model. Each call to .chat() returns the next
    scripted response. The script encodes one full Thought->Action->Answer
    flow for the task: "What is (2 + 3) * 4?"

    A real provider client would replace this whole class. The control loop
    below would not change.
    """

    def __init__(self, script: list[LLMResponse]) -> None:
        self._script = list(script)
        self._i = 0

    def chat(self, messages: list[dict[str, Any]]) -> LLMResponse:
        # We do *not* use `messages` -- the script is fixed. A real model
        # would, of course, condition its reply on the full message list.
        if self._i >= len(self._script):
            raise RuntimeError("FakeLLM script exhausted -- model would loop")
        resp = self._script[self._i]
        self._i += 1
        return resp


# ---------------------------------------------------------------------------
# 3. Observability
# ---------------------------------------------------------------------------
# One structured log line per loop step. In production this is an OTEL span
# exported to Langfuse/Phoenix; here it is just `print`. The shape matters
# more than the transport -- every step is greppable and replayable.


def log(**fields: Any) -> None:
    parts = [f"{k}={json.dumps(v) if isinstance(v, (dict, list)) else v}" for k, v in fields.items()]
    print(" ".join(parts))


# ---------------------------------------------------------------------------
# 4. Control loop
# ---------------------------------------------------------------------------
# This is the harness. Explicit, bounded, debuggable. Frameworks may hide
# this code -- our on-call cannot read framework internals at 2am, so we
# keep it.


@dataclass
class Budget:
    max_turns: int = 6              # hard cap on loop iterations
    max_tokens: int = 500           # hard cap on cumulative token estimate
    tokens_spent: int = 0           # running tally updated each turn

    def spend(self, n: int) -> None:
        self.tokens_spent += n

    def exhausted(self) -> tuple[bool, str]:
        if self.tokens_spent >= self.max_tokens:
            return True, f"token budget exhausted ({self.tokens_spent}/{self.max_tokens})"
        return False, ""


SYSTEM_PROMPT = (
    "You are a calculator agent. Use the available tools to compute the answer. "
    "When you have the final number, return it as the final answer."
)


def _tool_catalog() -> list[dict[str, Any]]:
    """The slice of context that advertises tools to the model."""
    return [{"name": t.name, "description": t.description, "schema": t.schema} for t in TOOLS.values()]


def run_agent(task: str, model: FakeLLM, budget: Budget | None = None) -> str:
    """
    The harness. Assembles context, calls the model, dispatches tool calls,
    enforces budgets, returns the final answer or raises on exhaustion.
    """
    budget = budget or Budget()

    # Named context slices -- the "assemble in replaceable slots" pattern
    # from 03-practice.md. Each slot can be swapped/tested independently.
    messages: list[dict[str, Any]] = [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "system", "content": f"tools: {json.dumps(_tool_catalog())}"},
        {"role": "user", "content": task},
    ]

    log(event="run_start", task=task, max_turns=budget.max_turns, max_tokens=budget.max_tokens)

    for turn in range(1, budget.max_turns + 1):
        # --- Budget check BEFORE the call: budgets are caps, not metrics.
        done, reason = budget.exhausted()
        if done:
            raise RuntimeError(f"graceful stop: {reason}")

        # --- Model call. The only non-deterministic step.
        resp = model.chat(messages)
        budget.spend(resp.tokens_used)

        # --- Decision: tool call vs. final answer.
        if resp.kind == "final":
            log(turn=turn, action="final", content=resp.content,
                tokens_used=resp.tokens_used, tokens_spent=budget.tokens_spent)
            log(event="run_end", outcome="success", turns=turn, tokens=budget.tokens_spent)
            return resp.content or ""

        if resp.kind == "tool_call":
            # Validate + dispatch through the registry. The model never
            # touches Python; the harness is the only thing that does.
            try:
                result = invoke_tool(resp.tool or "", resp.args or {})
            except ValueError as e:
                # Recoverable: feed the error back so the model can self-correct.
                log(turn=turn, action="tool_error", tool=resp.tool, error=str(e))
                messages.append({"role": "tool", "name": resp.tool, "content": f"error: {e}"})
                continue

            log(turn=turn, action="tool_call", tool=resp.tool, args=resp.args,
                result=result, tokens_used=resp.tokens_used, tokens_spent=budget.tokens_spent)

            # Append both the model's request and the tool result to history.
            messages.append({"role": "assistant", "tool": resp.tool, "args": resp.args})
            messages.append({"role": "tool", "name": resp.tool, "content": str(result)})
            continue

        raise RuntimeError(f"unknown response kind: {resp.kind!r}")

    # Fell out of the loop -- the model never produced a final answer.
    raise RuntimeError(f"graceful stop: max_turns reached ({budget.max_turns})")


# ---------------------------------------------------------------------------
# 5. Demo: one task end-to-end
# ---------------------------------------------------------------------------
# Task: "What is (2 + 3) * 4?"
# Scripted flow:
#   turn 1: model calls add(2, 3)              -> harness computes 5
#   turn 2: model calls multiply(5, 4)         -> harness computes 20
#   turn 3: model returns final answer "20"


def main() -> None:
    script = [
        LLMResponse(kind="tool_call", tool="add",      args={"a": 2, "b": 3}, tokens_used=40),
        LLMResponse(kind="tool_call", tool="multiply", args={"a": 5, "b": 4}, tokens_used=40),
        LLMResponse(kind="final",     content="The answer is 20.",            tokens_used=30),
    ]
    model = FakeLLM(script)

    answer = run_agent("What is (2 + 3) * 4?", model=model)
    print("---")
    print(f"final answer returned to caller: {answer!r}")


if __name__ == "__main__":
    main()
