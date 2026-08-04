---
description: Autonomously pick a new topic the repo is missing, run the full /learn pipeline on it, write a plain-English TOPIC_SUMMARIZATION.md at the repo root, then commit and push as `[CLAUDE] [<category>] <topic>`.
allowed-tools: Read, Write, Edit, Bash, Glob, Grep, Task, WebSearch, WebFetch
model: opus
---

# /daily-learn — Daily Autonomous Learning

You are running a fully autonomous daily learning loop. **No prompts to the user during execution.** Pick, learn, summarize, commit, push. Stop on the first failure.

## Stage 0 — Pick a topic

Invoke the `topic-picker` skill. Capture its three outputs:

- `PICKED_TOPIC` (human-readable name)
- `PICKED_CATEGORY` (one of the six standard categories — used only as a hint for downstream classification)
- `RATIONALE` (one line, log only)

Print one short line acknowledging the pick (e.g. `Today's topic: Service Worker (frontend) — <rationale>`) and proceed.

## Stage 1 — Run the /learn pipeline

Execute the orchestration described in `.claude/commands/learn.md` verbatim, with `$ARGUMENTS = PICKED_TOPIC`. That covers:

- Stage 0 of `/learn`: invoke `topic-folder-manager` — capture `CATEGORY`, `SLUG`, `PATH`, `NEXT_STEP`. From this point on, `CATEGORY` from `topic-folder-manager` is authoritative — it may differ from `PICKED_CATEGORY` and that is fine.
- Stages 1–5 of `/learn`: `overview-explainer` → `deep-analyzer` → `practitioner` → `code-implementer` → `present-builder`. The final stage writes the topic's `present/` HTML pages and regenerates the root dashboard (`node scripts/gen-dashboard.mjs`); the `git add -A` in Stage 3 below will pick up both the new `present/` folder and the regenerated `index.html`.

If `/learn` aborts at any stage (a quality gate fails, a subagent writes nothing), **stop the whole `/daily-learn` command immediately**. Do not write the summary file, do not commit, do not push. Print which stage failed and exit.

If Stage 0 of `/learn` returns `NEXT_STEP: complete`, the picker has somehow chosen an existing topic. Treat that as a picker bug — print a short note (`Picker collision on '<slug>'; aborting.`) and stop. Do not pick again on the same run.

## Stage 2 — Write TOPIC_SUMMARIZATION.md

After Stage 1 completes successfully, read `<CATEGORY>/<SLUG>/docs/01-overview.md` and distill it into a plain-English summary at `<repo root>/TOPIC_SUMMARIZATION.md`. **Overwrite** any existing file at that path.

### Resolving the deployed page URL

The presentation site is deployed to GitHub Pages at `https://quang-dobe.github.io/Practice.Agents-Concepts/`. Every topic's `present/index.html` is reachable at:

```
https://quang-dobe.github.io/Practice.Agents-Concepts/<CATEGORY>/<SLUG>/present/index.html
```

Call that `PAGE_URL`. Do not derive it from `git remote get-url origin` — the remote path may drift (renames, redirects); the Pages URL above is the stable canonical link.

### File template

Write exactly this shape — no YAML front matter, no extra sections:

```markdown
# <Topic Title>

<Paragraph 1: in plain language, what this topic IS. One concept, one sentence-or-two.>

<Paragraph 2: why it matters / when an engineer reaches for it.>

<Paragraph 3: one concrete example or analogy that grounds it.>

---

Full notes: <PAGE_URL>
```

Constraints:

- Total length 150–300 words across the three paragraphs.
- Use plain English. No bullets, no headings beyond `# <Topic Title>`.
- Do not invent content. Everything must be derivable from `01-overview.md` — this file is a condensation, not an extension.
- The last line is the raw URL (no markdown link syntax). Gmail will linkify it; markdown links don't render in a plain-text email body, which is what the GitHub Action sends.

## Stage 3 — Commit and push

Run, in this order:

```bash
git add -A
git status --porcelain
```

If `git status --porcelain` shows no changes, abort with `Nothing to commit — earlier stages may have silently no-op'd.` Do **not** create an empty commit.

Otherwise:

```bash
git commit -m "[CLAUDE] [<CATEGORY>] <PICKED_TOPIC>"
git push
```

`<CATEGORY>` is the one returned by `topic-folder-manager`, lowercase. `<PICKED_TOPIC>` is the human-readable name from the picker, used verbatim.

If `git push` fails (network, auth, non-fast-forward), report the error and stop. Do not retry — the user will resolve it manually.

## Final output

When all three stages complete successfully, print one clean line:

```
✓ [CLAUDE] [<CATEGORY>] <PICKED_TOPIC> — pushed.
```

Nothing else. No celebration, no doc summaries, no "hope this helps."

## Things you must NOT do

- Do not ask the user any question during this command. It is fully autonomous by design.
- Do not pick a different topic if `/learn` fails partway. Stop instead — half-finished topic folders are a debugging signal, not a thing to paper over.
- Do not commit `TOPIC_SUMMARIZATION.md` on its own. The commit must include the new topic folder too, which is why `git add -A` runs before the commit.
- Do not amend or force-push. Always a fresh commit.
- Do not edit `TOPICS.md` by hand here — `topic-folder-manager` already rebuilt it during Stage 1.
