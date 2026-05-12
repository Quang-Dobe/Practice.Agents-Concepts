---
name: topic-folder-manager
description: Use this skill whenever a learning workflow needs to figure out where on disk a new or existing topic should live. Handles classification of a topic into one of the five category folders (frontend, backend, ai, database, cloud), generation of a clean folder slug, and creation of the standard layout (`docs/` and `code/` subfolders). Trigger whenever a learning command needs to resolve a topic name to a filesystem path.
---

# Topic Folder Manager

This skill is the **router** for the learning repo. Every topic the user learns about lives in a predictable place. This skill makes that placement consistent.

## The repo layout this skill assumes

```
<repo root>/
├── frontend/
├── backend/
├── ai/
├── database/
├── cloud/
└── .claude/
```

Each category folder holds one subfolder per topic. Each topic subfolder holds `docs/` and `code/`:

```
backend/
└── circuit-breaker/
    ├── docs/
    │   ├── 01-overview.md
    │   ├── 02-deep-dive.md
    │   └── 03-practice.md
    └── code/
        ├── mvp.py
        └── README.md
```

If any of the five category folders does not yet exist at the repo root, create it before proceeding.

## How to classify a topic into a category

Use the following rules **in order**. Stop at the first match.

| Category | Belongs here if the topic is primarily about… |
|---|---|
| `ai` | Machine learning, LLMs, embeddings, RAG, vector search, prompt engineering, agents, transformers, training, fine-tuning, model serving, MLOps. |
| `database` | Storage engines, query languages, indexing, transactions, replication, sharding, schema design — for SQL **or** NoSQL stores (Postgres, MySQL, MongoDB, Redis, Cassandra, DynamoDB, etc.). Caching belongs here when it's *data caching* at the storage layer. |
| `cloud` | IaaS / PaaS / serverless, container orchestration, networking primitives at the cloud level, IaC, observability stacks, CDN, message brokers as managed services, multi-region patterns. |
| `frontend` | Browser, DOM, rendering, CSS, frontend frameworks (React, Vue, Svelte), client-side state, SPA/SSR/SSG architecture, web performance, accessibility, PWA. |
| `backend` | Everything else server-side: APIs, auth, business logic patterns, distributed-systems primitives that aren't tied to a specific cloud, server-side frameworks, protocols (HTTP, gRPC, WebSocket), security primitives like JWT. **Default category** when a topic spans multiple layers and you're unsure. |

### Ambiguity rules

- A topic that touches multiple categories goes into the category where it *most often appears as the primary concern*. Example: **JWT** spans auth + frontend + backend, but it lives in `backend/` because that's where it's designed and validated.
- **Caching**: HTTP / browser caching → `frontend`. Application-level caching (Redis, in-process) → `backend`. CDN caching → `cloud`. Query-result caching at the DB layer → `database`.
- **Message queues**: as a protocol concept → `backend`. As managed services (SQS, Pub/Sub) → `cloud`.
- **Observability**: tracing/logging principles → `backend`. Managed observability stacks (Datadog, Honeycomb config) → `cloud`.

If after these rules a topic is still genuinely 50/50, default to `backend/` and note the choice in the eventual overview doc. Do not ask the user to disambiguate — pick and move on.

## How to generate the folder slug

Take the topic name the user provided and:

1. Lowercase it.
2. Replace any run of non-alphanumeric characters with a single hyphen.
3. Strip leading/trailing hyphens.
4. If the result is empty or starts with a digit, prepend `topic-`.

Examples:
- `"Circuit Breaker"` → `circuit-breaker`
- `"OAuth 2.0"` → `oauth-2-0`
- `"gRPC"` → `grpc`
- `"What is RAG?"` → `what-is-rag` (but for this kind of question-form input, **strip leading interrogatives** first: drop `what is`, `how does`, `why is`, etc., giving `rag`).

## How to create the topic folder

Once you have `<category>` and `<slug>`:

1. Check if `<category>/<slug>/` already exists.
2. **If it exists**, do not overwrite — return the existing path. The orchestrating command will decide whether to resume or refuse.
3. **If it does not exist**, create:
   - `<category>/<slug>/`
   - `<category>/<slug>/docs/`
   - `<category>/<slug>/code/`
4. Return the absolute path to `<category>/<slug>/` and the relative path from the repo root.

## What you return

When invoked, you produce a short structured response in this exact form (whether the folder was new or pre-existing):

```
TOPIC: <human-readable topic name>
CATEGORY: <one of: frontend | backend | ai | database | cloud>
SLUG: <slug>
PATH: <category>/<slug>
ABSOLUTE_PATH: <absolute path>
STATUS: <created | existed>
NEXT_STEP: <docs/01-overview.md | docs/02-deep-dive.md | docs/03-practice.md | code/mvp.* | complete>
```

`NEXT_STEP` should reflect the **first missing artifact** in the standard pipeline so the orchestrating command knows where to resume:
- If `docs/01-overview.md` is missing → `docs/01-overview.md`
- Else if `docs/02-deep-dive.md` is missing → `docs/02-deep-dive.md`
- Else if `docs/03-practice.md` is missing → `docs/03-practice.md`
- Else if no file matching `code/mvp.*` exists → `code/mvp.*`
- Else → `complete`

That is the entire output of this skill. No commentary, no narration.
