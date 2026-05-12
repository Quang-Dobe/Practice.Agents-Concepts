---
description: Run only the overview stage of the learning pipeline for a topic.
argument-hint: <topic name>
allowed-tools: Read, Write, Edit, Bash, Glob, Grep, Task, WebSearch, WebFetch
model: opus
---

# /learn-overview — Stage 1 only

Topic: **$ARGUMENTS**

If `$ARGUMENTS` is empty, stop and ask for a topic name.

## Steps

1. Invoke the `topic-folder-manager` skill to resolve the topic folder. Capture `<topic-folder>` and its absolute path.
2. If `<topic-folder>/docs/01-overview.md` already exists, ask the user once whether to **regenerate** (overwrite) or **stop**. Default to stop.
3. Otherwise, invoke the **`overview-explainer`** subagent with the topic name and absolute path.
4. Verify `01-overview.md` was created and is non-trivial (>200 bytes).
5. Print a single line: `✓ Overview written to <topic-folder>/docs/01-overview.md`.

Do not run any of the later stages. Do not summarize the file content.
