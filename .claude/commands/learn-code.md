---
description: Run only the MVP-code stage of the learning pipeline.
argument-hint: <topic name>
allowed-tools: Read, Write, Edit, Bash, Glob, Grep, Task, WebSearch, WebFetch
model: opus
---

# /learn-code — Stage 4 only

Topic: **$ARGUMENTS**

If `$ARGUMENTS` is empty, stop and ask for a topic name.

## Steps

1. Invoke the `topic-folder-manager` skill to resolve the topic folder.
2. Verify all three prior docs exist (`01-overview.md`, `02-deep-dive.md`, `03-practice.md`). If any is missing, print:
   > Prior docs missing. The code stage relies on them to pick the right scope. Run `/learn <topic>` for the full pipeline.

   …and stop.
3. If `<topic-folder>/code/` already contains a `mvp.*` file, ask the user once whether to **regenerate** or **stop**. Default to stop.
4. Otherwise, invoke the **`code-implementer`** subagent with the topic name and absolute path.
5. Verify a `mvp.*` file and a `README.md` were created under `<topic-folder>/code/`.
6. Print a single line: `✓ MVP code written to <topic-folder>/code/`.

Do not summarize the code content. The reader can open it.
