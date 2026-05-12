---
description: Run only the real-world best-practices stage of the learning pipeline.
argument-hint: <topic name>
allowed-tools: Read, Write, Edit, Bash, Glob, Grep, Task, WebSearch, WebFetch
model: opus
---

# /learn-practice — Stage 3 only

Topic: **$ARGUMENTS**

If `$ARGUMENTS` is empty, stop and ask for a topic name.

## Steps

1. Invoke the `topic-folder-manager` skill to resolve the topic folder.
2. Verify both `01-overview.md` and `02-deep-dive.md` exist. If either is missing, print:
   > Prior docs missing. Run `/learn <topic>` for the full pipeline, or run the earlier stages individually first.

   …and stop.
3. If `<topic-folder>/docs/03-practice.md` already exists, ask the user once whether to **regenerate** or **stop**. Default to stop.
4. Otherwise, invoke the **`practitioner`** subagent with the topic name and absolute path.
5. Verify `03-practice.md` was created and is non-trivial (>500 bytes).
6. Print a single line: `✓ Practice doc written to <topic-folder>/docs/03-practice.md`.

Do not run later stages. Do not summarize the file content.
