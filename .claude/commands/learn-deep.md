---
description: Run only the deep-dive stage (What/Where/When/How/Why) of the learning pipeline.
argument-hint: <topic name>
allowed-tools: Read, Write, Edit, Bash, Glob, Grep, Task, WebSearch, WebFetch
model: opus
---

# /learn-deep — Stage 2 only

Topic: **$ARGUMENTS**

If `$ARGUMENTS` is empty, stop and ask for a topic name.

## Steps

1. Invoke the `topic-folder-manager` skill to resolve the topic folder.
2. Verify `<topic-folder>/docs/01-overview.md` exists. If it does not, print:
   > No overview found for this topic yet. Run `/learn-overview <topic>` first, or `/learn <topic>` for the full pipeline.

   …and stop.
3. If `<topic-folder>/docs/02-deep-dive.md` already exists, ask the user once whether to **regenerate** or **stop**. Default to stop.
4. Otherwise, invoke the **`deep-analyzer`** subagent with the topic name and absolute path.
5. Verify `02-deep-dive.md` was created and is non-trivial (>500 bytes — deep dives are long).
6. Print a single line: `✓ Deep dive written to <topic-folder>/docs/02-deep-dive.md`.

Do not run later stages. Do not summarize the file content.
