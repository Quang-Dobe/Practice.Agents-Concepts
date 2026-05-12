# Learning Repo — Claude Context

This repository is a personal tech-learning notebook. Each topic the user studies lives in its own folder, organized by category, with a fixed four-document structure.

## Repository layout

```
<repo root>/
├── frontend/         # browser, frameworks, rendering, client-side
├── backend/          # APIs, auth, distributed-systems primitives
├── ai/               # ML, LLMs, RAG, embeddings, agents
├── database/         # SQL + NoSQL, storage engines, indexing
├── cloud/            # IaaS/PaaS, serverless, containers, IaC
├── general-concept/  # cross-cutting SE ideas (CAP, idempotency, SOLID, …)
└── .claude/          # agents, skills, slash commands (this folder)
```

The six category folders above are the **only valid landing sites** the `topic-folder-manager` skill will produce, with one narrow exception: any extra top-level directory the user has manually created at the repo root is also treated as a valid category, so the skill respects pre-existing structure rather than re-classifying it.

Each category contains one folder per topic. Each topic folder follows this exact structure:

```
<category>/<topic-slug>/
├── docs/
│   ├── 01-overview.md     # easy intuition-first explanation
│   ├── 02-deep-dive.md    # What / Where / When / How / Why
│   └── 03-practice.md     # real-world best practices + anti-patterns
└── code/
    ├── mvp.<ext>          # minimal runnable demo
    └── README.md          # how to run it
```

Filenames `01-`, `02-`, `03-` are deliberately ordered for natural reading order in GitHub.

## How the user works in this repo

The user picks a topic each day, runs `/learn <topic>`, and ends up with a complete topic folder. They can also run any single stage with `/learn-overview`, `/learn-deep`, `/learn-practice`, or `/learn-code`, and check overall progress with `/learn-status`.

The pipeline is opinionated on purpose: every topic folder should look the same so the user can build muscle memory for where to find things.

## Conventions you must follow

- **Do not invent new filenames.** The four files above are the contract. No `requirements.md`, no `analyzed.md`, no `summary.md`. If something genuinely doesn't fit, raise it to the user instead of silently adding files.
- **Always use the `topic-folder-manager` skill** to resolve a topic to a folder path. Do not classify topics ad-hoc. The skill runs a **web-search-informed** classifier for unfamiliar keywords, but its output is **bounded** to the six standard categories (or a category folder that already exists at the repo root). New top-level categories are never invented from a single web search.
- **Always use the `learning-doc-formatter` skill's conventions** when writing any document in a topic folder. Specifically: no YAML front matter in docs, no banned marketing words, no "Hope this helps!" sign-offs.
- **Language defaults for MVP code are fixed by category** — and the `code-implementer` agent must load the matching convention skill before writing code:
  - `frontend/` → **TypeScript** → load `typescript-frontend-conventions` skill
  - `backend/` → **.NET 8+ / C#** with Clean Architecture + a hand-rolled custom Mediator (no MediatR / no third-party CQRS libs) → load `dotnet-backend-conventions` skill
  - `database/` → **.NET** for app-side code, plain `.sql` for schema/query topics → load `dotnet-backend-conventions` when writing C#
  - `cloud/` → **.NET** for SDK-driven code, **TypeScript** for IaC/edge, YAML/HCL for declarative IaC → load whichever skill matches
  - `ai/` → **Python 3.11+** (no convention skill needed beyond PEP-8)
  - `general-concept/` → **Python 3.11+** by default (reads close to pseudocode). The agent may pick another language if the deep-dive doc shows the concept is intrinsically tied to a specific stack — in that case load the matching convention skill.
- **The four pipeline subagents (`overview-explainer`, `deep-analyzer`, `practitioner`, `code-implementer`) all run on Opus.** That is intentional — this is personal deep learning, not throughput work. Don't override their model.
- Each subagent reads the prior docs in the topic folder before writing its own. They share scope and terminology through the files, not through chat.

## Style for the docs themselves

- Direct, declarative, no hedging filler.
- Concrete examples beat abstract claims.
- Length budgets exist for a reason — overview ~400–700 words, deep dive ~1200–2500 words, practice ~1000–2000 words, code README under 40 lines.
- Code blocks inside `.md` docs are *illustrative*. The runnable code lives in `code/mvp.*`.

## When in doubt

When in doubt about classification between two **stack-bound** categories, default to `backend/`. When a topic is clearly a cross-cutting principle with no natural home in a single layer (CAP theorem, idempotency, SOLID, dependency-injection-as-a-concept), it belongs in `general-concept/` — not in `backend/` as a catch-all.

When in doubt about a fact, search the web — this is a learning repo, getting things subtly wrong defeats the entire point.
