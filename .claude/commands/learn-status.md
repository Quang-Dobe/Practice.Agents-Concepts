---
description: Show the status of every topic in the learning repo — which stages are complete, which are missing.
allowed-tools: Read, Bash, Glob, Grep
model: opus
---

# /learn-status — Repo-wide progress report

You are producing a compact, scannable report of every topic in the learning repo.

## Steps

1. Build the category list:
   - Start with the six standard categories in this order: `frontend`, `backend`, `ai`, `database`, `cloud`, `general-concept`.
   - Append any extra top-level directories at the repo root that are not dot-folders and not one of the six above (alphabetically sorted). This catches user-created categories like `devops/`.
2. For each category in that list:
   - If the category folder does not exist, skip it.
   - Otherwise, list every immediate subfolder. Each such subfolder is a topic.
3. For each topic, check which of the standard artifacts exist:
   - `docs/01-overview.md`
   - `docs/02-deep-dive.md`
   - `docs/03-practice.md`
   - `code/mvp.*` (any extension)
4. Render a single Markdown report grouped by category, in the same order as step 1.

## Output format

```
# Learning Repo Status

## frontend (N topics)
| Topic | Overview | Deep | Practice | Code |
|---|---|---|---|---|
| react-suspense    | ✓ | ✓ | ✓ | ✓ |
| css-container-q   | ✓ | ✓ | – | – |
| ...

## backend (N topics)
| Topic | Overview | Deep | Practice | Code |
|---|---|---|---|---|
| circuit-breaker   | ✓ | ✓ | ✓ | ✓ |
| ...

(repeat for ai, database, cloud, general-concept, then any extra user-created categories)

## Summary
- Total topics: <N>
- Fully complete (4/4 artifacts): <N>
- In progress: <N>
- Just started (overview only): <N>
```

Use `✓` for present, `–` for missing. Sort topics alphabetically within each category. If a category has zero topics, list it as `## <category> (0 topics) — none yet` and skip the table.

Keep the entire report tight. No commentary, no recommendations, just the table and the summary block.
