---
description: Full learning pipeline for a new tech topic — overview → deep dive → practice → MVP code.
argument-hint: <topic name>
allowed-tools: Read, Write, Edit, Bash, Glob, Grep, Task, WebSearch, WebFetch
model: opus
---

# /learn — Full Learning Pipeline

You are orchestrating a four-stage learning workflow for the topic: **$ARGUMENTS**

If `$ARGUMENTS` is empty, stop and ask the user for a topic name before doing anything else.

## Your job as orchestrator

You are **not** writing the docs yourself. You are coordinating the specialist subagents and skills below in sequence. Each stage produces one artifact on disk; you do not summarize those artifacts in chat — you simply confirm they exist and move on.

## The pipeline

### Stage 0 — Resolve the topic folder
Invoke the `topic-folder-manager` skill with the topic name `$ARGUMENTS`. It will:
- classify the topic into one of `frontend / backend / ai / database / cloud / general-concept` (or any existing extra category folder already at the repo root) — running a focused web search first for unfamiliar keywords
- generate a folder slug
- create `<category>/<slug>/docs/` and `<category>/<slug>/code/` if missing
- return the path and the next missing artifact

Capture `CATEGORY`, `SLUG`, `PATH`, and `NEXT_STEP` from its response. From here on, refer to the topic folder as `<topic-folder>` = `<CATEGORY>/<SLUG>`.

If `NEXT_STEP` is `complete`, all four artifacts already exist. Print:
> All four artifacts for `<topic-folder>` already exist. Use `/learn-overview <topic>`, `/learn-deep <topic>`, `/learn-practice <topic>`, or `/learn-code <topic>` to regenerate a specific stage.

…and stop.

Otherwise, **resume from `NEXT_STEP`** and run every remaining stage in order.

### Stage 1 — Overview (if missing)
Use the **`overview-explainer`** subagent. Pass it the topic name and the absolute path to `<topic-folder>`. Wait for it to confirm `01-overview.md` has been written.

### Stage 2 — Deep dive (if missing)
Use the **`deep-analyzer`** subagent. Pass it the topic name and the absolute path to `<topic-folder>`. It will read `01-overview.md` itself to align scope. Wait for confirmation that `02-deep-dive.md` exists.

### Stage 3 — Practice (if missing)
Use the **`practitioner`** subagent. Pass it the topic name and the absolute path to `<topic-folder>`. It will read both prior docs. Wait for confirmation that `03-practice.md` exists.

### Stage 4 — MVP code (if missing)
Use the **`code-implementer`** subagent. Pass it the topic name and the absolute path to `<topic-folder>`. It will read all three prior docs. Wait for confirmation that `code/mvp.*` and `code/README.md` exist.

## Quality gates between stages

Between each stage:
1. Verify the previous stage's file actually exists on disk (use `Glob` or `Read`).
2. Verify it is not empty (size > 200 bytes is a reasonable floor — an aborted write would be smaller).
3. If a stage failed to produce its output, **stop the pipeline**, report which stage failed, and do not proceed to the next.

## Final output

When all four stages have completed successfully, print a single, clean summary to chat — no decoration, no celebration:

```
✓ <Topic Name> → <topic-folder>/
  ├── docs/01-overview.md
  ├── docs/02-deep-dive.md
  ├── docs/03-practice.md
  └── code/mvp.<ext> + README.md

Open <topic-folder>/docs/01-overview.md to start reading.
```

That is the whole final message. Do not include explanations of what each doc contains — the user can open them.

## Things you must NOT do

- Do not write the doc content yourself. That's what the subagents are for, and they each run on Opus with their own context window. Doing it in the orchestrator wastes context.
- Do not paste long quotes from the generated docs into chat.
- Do not ask the user for clarification mid-pipeline. The agents are designed to make defensible choices and proceed. The only legitimate stop is a failed write.
- Do not create files outside the standard layout defined by the `learning-doc-formatter` skill. No `requirement.md`, no `analyzed.md`, no `summary.md`.
- Do not invoke the subagents in parallel. They depend on each other's outputs in order.
