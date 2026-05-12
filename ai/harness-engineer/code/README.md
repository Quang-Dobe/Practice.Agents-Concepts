# Harness Engineer — MVP Code

The smallest runnable demo of an LLM **harness**: the deterministic software
loop around a (mocked) model that turns it into a tool-using agent. About
~110 lines of actual Python, comments excluded.

## What it demonstrates
- **`Agent = Model + Harness`** — the model is a single, swappable function call (`FakeLLM.chat`); everything else (loop, registry, validation, budgets, logs) is harness code.
- **Explicit control loop** — `run_agent()` owns turn counting, tool dispatch, stopping conditions, and graceful failure. No framework magic.
- **Typed tool registry** — each tool has a name, JSON-schema-ish contract, and a Python callable; args are validated before invocation so malformed calls become recoverable loop steps.
- **Budgets as hard caps** — `max_turns` and `max_tokens` are enforced *inside* the loop, not just logged.
- **Observability** — one structured `key=value` line per step, greppable and replayable end-to-end.

## Prerequisites
Python 3.11+. **Zero external dependencies** — standard library only.

## Run it

```bash
python mvp.py
```

## Expected output

```
event=run_start task=What is (2 + 3) * 4? max_turns=6 max_tokens=500
turn=1 action=tool_call tool=add args={"a": 2, "b": 3} result=5 tokens_used=40 tokens_spent=40
turn=2 action=tool_call tool=multiply args={"a": 5, "b": 4} result=20 tokens_used=40 tokens_spent=80
turn=3 action=final content=The answer is 20. tokens_used=30 tokens_spent=110
event=run_end outcome=success turns=3 tokens=110
---
final answer returned to caller: 'The answer is 20.'
```

Trace the three turns: two tool calls, one final answer. That sequence is the harness loop in its entirety.

## Why the model is mocked
The whole point of harness engineering is that the loop, registry, validation, and budgets are valuable *independently of the model* — so we replace the model with a 15-line scripted stub and let the harness do real work offline.

## What to try next
- Drop `Budget.max_tokens` to `50` and observe the graceful-stop exception fire mid-run.
- Change `multiply`'s scripted args to `{"a": "five", "b": 4}` and watch the validator route the error back as a recoverable step.
- Add a new tool (e.g. `subtract`) with `@register(...)` and a fourth script entry — note that the loop code does not change.
- Set `max_turns=2` and see the loop terminate with `max_turns reached` instead of a final answer.
