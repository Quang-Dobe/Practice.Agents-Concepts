# `.claude/` — Personal Learning Pipeline

A Claude Code configuration that turns any tech topic into a complete, well-structured study folder with one command.

## What it does

Type `/learn <topic>` in Claude Code and end up with a folder like this:

```
backend/circuit-breaker/
├── docs/
│   ├── 01-overview.md     ← intuition-first, ~5 min read
│   ├── 02-deep-dive.md    ← What / Where / When / How / Why
│   └── 03-practice.md     ← real-world best practices + anti-patterns
└── code/
    ├── mvp.py             ← minimal runnable demo (~50 LOC)
    └── README.md          ← how to run it
```

The topic is automatically classified into one of `frontend / backend / ai / database / cloud`. The folder slug is generated from the topic name. Each stage uses a dedicated Opus subagent so the main session stays light.

## File map

```
.claude/
├── CLAUDE.md                                    # project-level context, loaded every session
├── README.md                                    # this file
├── agents/
│   ├── overview-explainer.md                    # stage 1: intuition-first explanation
│   ├── deep-analyzer.md                         # stage 2: What/Where/When/How/Why
│   ├── practitioner.md                          # stage 3: real-world practices
│   └── code-implementer.md                      # stage 4: MVP code
├── skills/
│   ├── topic-folder-manager/SKILL.md            # classifies topics & creates folders
│   ├── learning-doc-formatter/SKILL.md          # shared style/quality rules for docs
│   ├── typescript-frontend-conventions/SKILL.md # TS style, type rules, React patterns
│   └── dotnet-backend-conventions/SKILL.md      # .NET style, Clean Arch, custom Mediator
└── commands/
    ├── learn.md                                 # full pipeline orchestrator
    ├── learn-overview.md                        # stage 1 only
    ├── learn-deep.md                            # stage 2 only
    ├── learn-practice.md                        # stage 3 only
    ├── learn-code.md                            # stage 4 only
    └── learn-status.md                          # repo-wide progress report
```

## Commands

| Command | What it does |
|---|---|
| `/learn <topic>` | Full pipeline: overview → deep dive → practice → MVP code. Resumes from the first missing stage if the topic folder already exists partially. |
| `/learn-overview <topic>` | Just the intuition-first overview. |
| `/learn-deep <topic>` | Just the deep dive (requires overview to exist). |
| `/learn-practice <topic>` | Just the best-practices doc (requires overview + deep dive). |
| `/learn-code <topic>` | Just the MVP code (requires all three docs). |
| `/learn-status` | Markdown table of every topic in the repo and which stages are complete. |

## How the four stages work

| Stage | Subagent | Output | Length |
|---|---|---|---|
| 1 | `overview-explainer` | `docs/01-overview.md` | 400–700 words, analogy-driven |
| 2 | `deep-analyzer` | `docs/02-deep-dive.md` | 1200–2500 words, structured by What/Where/When/How/Why |
| 3 | `practitioner` | `docs/03-practice.md` | 1000–2000 words, best practices + anti-patterns + war stories |
| 4 | `code-implementer` | `code/mvp.<ext>` + `code/README.md` | 30–100 lines of actual code |

All four subagents run on **Opus** by default — this is for personal deep learning, not throughput.

Each subagent reads the prior docs in the topic folder before writing its own, so they share scope and terminology through the files instead of through chat. This keeps the orchestrator's context window small.

## Language defaults for MVP code

The `code-implementer` agent picks the language based on the topic's category:

| Category | Language | Convention skill loaded |
|---|---|---|
| `frontend` | **TypeScript 5.4+** (strict mode, ESM, React only when the topic is React) | `typescript-frontend-conventions` |
| `backend` | **.NET 8+ / C#** with **Clean Architecture** + a **hand-rolled custom Mediator** | `dotnet-backend-conventions` |
| `database` | **.NET / C#** for app-side code, plain `.sql` for schema or query-language topics | `dotnet-backend-conventions` |
| `cloud` | **.NET** for SDK-driven topics, **TypeScript** for IaC (CDK) and edge/worker topics, YAML/HCL for declarative IaC | whichever matches |
| `ai` | **Python 3.11+** (PEP-8, no extra skill needed) | — |

The two language-convention skills (`typescript-frontend-conventions` and `dotnet-backend-conventions`) cover coding style, naming, type rules, design patterns, and architectural defaults so every demo of the same category looks consistent.

**The .NET stack specifically** uses Clean Architecture with four projects (`Domain`, `Application`, `Infrastructure`, `Api`) and a tiny ~60-line hand-rolled Mediator under `Application/Mediator/`. **No MediatR, no MassTransit, no third-party CQRS libraries** — the point is to see the pattern, not learn a library. The full mediator implementation is spelled out in the skill so every backend demo wires through the same shape.

## How topic classification works

The `topic-folder-manager` skill applies these rules in order, stopping at the first match:

1. **`ai`** — ML, LLMs, embeddings, RAG, vector search, prompt engineering, agents, training, MLOps.
2. **`database`** — storage engines, query languages, indexing, transactions, replication. SQL or NoSQL.
3. **`cloud`** — IaaS/PaaS/serverless, container orchestration, IaC, managed services, CDN.
4. **`frontend`** — browser, DOM, rendering, CSS, frontend frameworks, SPA/SSR/SSG, accessibility.
5. **`backend`** — everything else server-side. Default when nothing else fits.

Ambiguous topics (e.g. caching, message queues, observability) have explicit tie-breakers in the skill. You can override classification by manually creating the topic folder in your preferred category before running `/learn`.

## Setup

1. Drop the entire `.claude/` folder into the root of the repo you want to use as your learning notebook.
2. Open the repo with Claude Code.
3. Run `/learn <some topic>` — for example `/learn circuit-breaker pattern` or `/learn RAG`.

That's it. No environment variables, no global config.

## Conventions

The four-doc structure is the contract. Agents will not invent new filenames like `requirements.md` or `summary.md`. If a topic genuinely needs something custom, raise it explicitly rather than letting an agent silently add files.

Each document has a length budget. The budgets exist to keep the docs skimmable — if a doc starts feeling bloated, the agent will cut padding before exceeding the budget.

Banned filler words: *robust, seamless, leverage, powerful, cutting-edge, delve, embark*. Direct, declarative writing only.

## Extending the system

- **Add a new category?** Edit the table in `skills/topic-folder-manager/SKILL.md`. Add the category to the layout section of `CLAUDE.md` and the loop in `commands/learn-status.md`.
- **Add a new document type?** Add an agent under `agents/`, add it to the pipeline in `commands/learn.md`, and update the filename whitelist in `skills/learning-doc-formatter/SKILL.md`.
- **Change the writing style?** Edit `skills/learning-doc-formatter/SKILL.md`. All four pipeline agents inherit from it.

## Cost note

Each `/learn` run invokes four Opus subagents in sequence, each doing its own web research. Expect a real token bill per run — this is a deliberate trade-off for depth over throughput. If you want a cheaper version, change `model: opus` to `model: sonnet` in the agent frontmatter.
