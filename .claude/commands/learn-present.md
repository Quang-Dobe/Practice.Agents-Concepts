---
description: Run only the present/ HTML stage of the learning pipeline — re-author the docs into the topic's dark-themed presentation pages.
argument-hint: <topic name>
allowed-tools: Read, Write, Edit, Bash, Glob, Grep, Task, WebSearch, WebFetch
model: opus
---

# /learn-present — Stage 5 only

Topic: **$ARGUMENTS**

If `$ARGUMENTS` is empty, stop and ask for a topic name.

## Steps

1. Invoke the `topic-folder-manager` skill to resolve the topic folder.
2. Verify all three docs exist (`01-overview.md`, `02-deep-dive.md`, `03-practice.md`). If any is missing, print:
   > Prior docs missing. The present stage re-authors them. Run `/learn <topic>` for the full pipeline.

   …and stop.
3. If `<topic-folder>/present/` already contains `index.html`, ask the user once whether to **regenerate** or **stop**. Default to stop.
4. Otherwise, invoke the **`present-builder`** subagent with the topic name and absolute path. It loads the `present-page-conventions` skill and writes the four pages.
5. Verify `index.html`, `overview.html`, `detail.html`, and `practice.html` were created under `<topic-folder>/present/`, and that `index.html` at the repo root was regenerated (the agent runs `node scripts/gen-dashboard.mjs`). If the agent did not run it, run it yourself.
6. Print a single line: `✓ present/ written to <topic-folder>/present/ and dashboard regenerated.`

Do not summarize the page content. The reader can open it.
