---
description: Show the status of every topic in the learning repo — which stages are complete, which are missing.
allowed-tools: Read, Bash, Glob, Grep
model: opus
---

# /learn-status — Repo-wide progress report

You are producing a compact, scannable report of every topic in the learning repo.

## Steps

1. For each of the five categories (`frontend`, `backend`, `ai`, `database`, `cloud`):
   - If the category folder does not exist, skip it.
   - Otherwise, list every immediate subfolder. Each such subfolder is a topic.
2. For each topic, check which of the standard artifacts exist:
   - `docs/01-overview.md`
   - `docs/02-deep-dive.md`
   - `docs/03-practice.md`
   - `code/mvp.*` (any extension)
3. Render a single Markdown report grouped by category.

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

(repeat for ai, database, cloud)

## Summary
- Total topics: <N>
- Fully complete (4/4 artifacts): <N>
- In progress: <N>
- Just started (overview only): <N>
```

Use `✓` for present, `–` for missing. Sort topics alphabetically within each category. If a category has zero topics, list it as `## <category> (0 topics) — none yet` and skip the table.

Keep the entire report tight. No commentary, no recommendations, just the table and the summary block.
