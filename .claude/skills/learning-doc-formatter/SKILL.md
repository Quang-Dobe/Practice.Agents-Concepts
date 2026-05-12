---
name: learning-doc-formatter
description: Use this skill any time a learning-pipeline agent (overview-explainer, deep-analyzer, practitioner, code-implementer) is writing a markdown document or code README into a topic folder. Defines the shared conventions for tone, structure, citation style, and quality bar across all learning docs so every topic folder reads consistently.
---

# Learning Doc Formatter

Every document in this learning repo is written by an agent. Every reader is **future you**. This skill exists so that future you can pick up any topic folder, in any category, six months from now, and immediately know where to find what.

It defines the **shared conventions** that apply across all four document types (`01-overview.md`, `02-deep-dive.md`, `03-practice.md`, and `code/README.md`). The individual agents own the *structure* of their respective documents; this skill owns the *style*.

## Hard rules (apply to every doc)

### 1. Front matter
Do **not** include YAML front matter in the learning docs themselves. The category and slug are already encoded in the file path. The H1 of the file should be the topic name plus the doc role, e.g. `# Circuit Breaker — Deep Dive`.

### 2. Tone
- Write for a senior engineer who does not yet know this specific topic.
- Direct, declarative, no hedging filler ("you might want to consider possibly looking into…").
- No marketing voice. Banned words and phrases:
  - *robust, seamless, leverage, powerful, cutting-edge, harness, unlock, in today's fast-paced world*
  - *delve, journey, embark, navigate the landscape of*
- First person is fine ("I would reach for…"); second person is fine ("you'll notice…"). Pick one per document and stay consistent.

### 3. Length discipline
Each agent has its own length budget. Treat the budget as a real constraint. The fix for "too long" is almost always to delete examples that say the same thing twice, not to compress sentences.

### 4. Code in docs
- Inline code (`backticks`) for identifiers, file names, commands, short snippets.
- Fenced code blocks with a language tag (` ```python `, ` ```sql `, ` ```bash `) for anything multi-line.
- Code blocks inside docs should be **illustrative, not runnable**. The runnable code lives in `code/mvp.*`. Reference it: "see `code/mvp.py` for a runnable version."

### 5. Diagrams
- Plain Markdown only. No external image dependencies.
- ASCII art is allowed when it genuinely helps spatial understanding (request flow, layered architecture). One diagram per doc is plenty; two is the ceiling.
- Mermaid blocks are allowed (` ```mermaid `) since GitHub renders them, but use sparingly — overuse becomes noise.

### 6. Links
- Inline links only, no reference-style footnotes.
- Link only to **primary sources**: official docs, RFCs, well-known engineering blogs, peer-reviewed papers.
- Bare URLs are fine when the URL itself is informative.
- Do not link to SEO-farm tutorials, listicles, or "top 10 X frameworks" pages.

### 7. Numbers and versions
Any specific number — default port, default isolation level, library version, complexity class — must be verified, not guessed. If verification was not possible, write the claim with explicit uncertainty ("commonly N, but version-dependent") rather than confidently asserting a stale value.

### 8. Cross-references between docs
Documents in the same topic folder may reference each other by relative path:
- From `02-deep-dive.md`: ``` see `01-overview.md` ```
- From `03-practice.md`: ``` see `02-deep-dive.md § How` ```
- From `code/README.md`: ``` see `../docs/02-deep-dive.md` ```

Do **not** cross-reference into other topics' folders. Each topic is self-contained.

### 9. Status notes are fine
If a section can't be filled in confidently for a particular topic (e.g. the topic has no real "history" worth covering), write one sentence: `_Not applicable for this topic._` and move on. Do not pad.

### 10. End every doc cleanly
Last line is the last meaningful sentence. No "Hope this helps!", no "Happy learning!", no horizontal rule + signature. Future you doesn't need pep talk.

## Per-document role check

Before any agent finishes writing, it should be able to answer:

- **`01-overview.md`** — Can a smart non-specialist read this in five minutes and explain the topic to a colleague? If not, simplify.
- **`02-deep-dive.md`** — If I asked the reader to draw the data flow / lifecycle / decision tree, could they? If not, the *How* section is too abstract.
- **`03-practice.md`** — Could a reader use this doc as a checklist during a code review of a system that uses the topic? If not, the *Best practices* section is too vague.
- **`code/README.md`** — Could someone clone the repo and run the demo in under five minutes without further questions? If not, prerequisites are missing.

## Suggested filename conventions

Inside each topic folder, **only these filenames are blessed**. Do not invent new ones unless explicitly asked:

```
docs/
├── 01-overview.md   # written by overview-explainer
├── 02-deep-dive.md  # written by deep-analyzer
├── 03-practice.md   # written by practitioner
└── (optional) 99-notes.md   # the user's own notes; agents never write here
code/
├── mvp.<ext>        # written by code-implementer
├── (optional second file, e.g. server.<ext>)
└── README.md        # written by code-implementer
```

If an agent feels the urge to create `requirements.md`, `analyzed.md`, `summary.md`, or any other file outside this list — **don't**. The four-doc structure is the contract. If something genuinely doesn't fit, raise it instead of silently adding new files.
