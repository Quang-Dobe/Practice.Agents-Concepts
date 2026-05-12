---
name: topic-folder-manager
description: Use this skill whenever a learning workflow needs to figure out where on disk a new or existing topic should live. Handles web-search-informed classification of a topic into one of the six bounded categories (frontend, backend, ai, database, cloud, general-concept) plus any existing category folders already at the repo root, generation of a clean folder slug, and creation of the standard layout (`docs/` and `code/` subfolders). Trigger whenever a learning command needs to resolve a topic name to a filesystem path.
---

# Topic Folder Manager

This skill is the **router** for the learning repo. Every topic the user learns about lives in a predictable place. This skill makes that placement consistent — even when the user feeds in a keyword whose category is not obvious from the name alone.

## The repo layout this skill assumes

```
<repo root>/
├── frontend/
├── backend/
├── ai/
├── database/
├── cloud/
├── general-concept/
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

If any of the six standard category folders does not yet exist at the repo root, create it before placing a topic in it.

## The valid set of categories

The classifier's output is **bounded**. A topic must land in one of:

1. The six **standard categories**: `frontend`, `backend`, `ai`, `database`, `cloud`, `general-concept`.
2. **Any extra top-level directory that already exists** at the repo root (anything that is not a dot-folder, not a file, and not one of the six above). These are categories the user manually created and the skill must respect them rather than re-classify their contents elsewhere.

Before classifying, list the entries at the repo root. Build the valid-category set as `{frontend, backend, ai, database, cloud, general-concept} ∪ {existing top-level non-dot directories}`. This set is the only legal output for `CATEGORY`. **Do not invent new categories** even when web search suggests an exotic-sounding domain — collapse it into the nearest member of the valid set.

## How to classify a topic — the dynamic protocol

Run these steps **in order**. Stop at the first confident match.

### Step 1 — Cheap name-based pass

If the topic name itself is a clear, well-known term that obviously matches one of the rules in the table below, take that decision and skip the web search. Examples: `react-suspense` → `frontend`, `b-tree index` → `database`, `kubernetes` → `cloud`. Do not burn a web search on the unambiguous.

### Step 2 — Web search for ambiguous or unknown keywords

If the keyword is unfamiliar, an acronym, a buzzword, or could plausibly belong in two or more categories, run **one focused web search** (e.g. `<keyword> what is`, `<keyword> software engineering`) and read the top 2–3 result snippets. You are looking for **one signal**: what kind of thing is this, and which layer of a software system does it live at?

You are not writing a definition — the `overview-explainer` agent does that later. The only job here is to inform a routing decision.

### Step 3 — Apply the classification rules

Use the following rules **in order**. Stop at the first match. The web-search signal from Step 2 feeds the cells.

| Category | Belongs here if the topic is primarily about… |
|---|---|
| `ai` | Machine learning, LLMs, embeddings, RAG, vector search, prompt engineering, agents, transformers, training, fine-tuning, model serving, MLOps. |
| `database` | Storage engines, query languages, indexing, transactions, replication, sharding, schema design — for SQL **or** NoSQL stores (Postgres, MySQL, MongoDB, Redis, Cassandra, DynamoDB, etc.). Caching belongs here when it's *data caching* at the storage layer. |
| `cloud` | IaaS / PaaS / serverless, container orchestration, networking primitives at the cloud level, IaC, observability stacks, CDN, message brokers as managed services, multi-region patterns. |
| `frontend` | Browser, DOM, rendering, CSS, frontend frameworks (React, Vue, Svelte), client-side state, SPA/SSR/SSG architecture, web performance, accessibility, PWA. |
| `backend` | Server-side things with a clear home in one stack: APIs, auth, business-logic patterns, distributed-systems primitives that live in app code, server-side frameworks, protocols (HTTP, gRPC, WebSocket), security primitives like JWT. |
| `general-concept` | **Fallback only.** Use when the topic is a cross-cutting software-engineering idea, principle, or vocabulary term that does not belong to any single layer of the stack — e.g. *CAP theorem, idempotency, eventual consistency as a principle, SOLID, DRY, dependency injection as a concept (not a framework), domain-driven design vocabulary, distributed-systems theorems, software-architecture taxonomies, generic patterns from the GoF book*. |
| `<existing-extra-dir>` | If a top-level directory already exists at the repo root (e.g. the user previously created `devops/`), and the web search shows the keyword is squarely in that domain, prefer it over `general-concept`. |

### Step 4 — Tie-break in favour of a standard category

If the web search shows the topic has any meaningful home in `frontend / backend / ai / database / cloud`, **pick that** — even if it also spans others. `general-concept` is reserved for topics that are intrinsically stack-agnostic.

Examples of the tie-break in action:
- `idempotency` → `general-concept` (a principle, not tied to one layer).
- `idempotency keys for payment APIs` → `backend` (concrete implementation in app code).
- `eventual consistency` (the theorem) → `general-concept`.
- `eventual consistency in DynamoDB` → `database` (concrete storage system).
- `CAP theorem` → `general-concept`.
- `gossip protocol implementation` → `backend`.

If after these rules a topic is genuinely 50/50 between two standard categories, default to `backend/`. **Do not ask the user to disambiguate** — pick and move on. The choice can be reflected in the eventual overview doc.

### Step 5 — Existing-folder override

Before finalising, check whether `<some-category>/<slug>/` already exists for the slug you generated. If it does, the topic has been placed before — return that existing path even if your classification would have put it elsewhere. The existing location wins; do not move folders silently.

## Ambiguity quick-reference (carried over from prior behavior)

- A topic that touches multiple categories goes into the category where it *most often appears as the primary concern*. Example: **JWT** spans auth + frontend + backend, but it lives in `backend/` because that's where it's designed and validated.
- **Caching**: HTTP / browser caching → `frontend`. Application-level caching (Redis, in-process) → `backend`. CDN caching → `cloud`. Query-result caching at the DB layer → `database`. Caching *as a general principle / cache-invalidation theory* → `general-concept`.
- **Message queues**: as a protocol concept → `backend`. As managed services (SQS, Pub/Sub) → `cloud`. *Queue-theory / back-pressure as a principle* → `general-concept`.
- **Observability**: tracing/logging principles → `backend`. Managed observability stacks (Datadog, Honeycomb config) → `cloud`. *Observability as an engineering discipline* → `general-concept`.

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

## Refresh the topic index — required final step

After the folder is created or confirmed to exist, and **before** returning the structured response, rewrite `<repo root>/TOPICS.md` from scratch so it reflects the current state of the repo. This keeps the index self-healing — it picks up topics that were renamed, added, or removed manually, and can never drift out of sync.

### How to rebuild

1. Build the category list:
   - Start with the six standard categories in this exact order: `frontend`, `backend`, `ai`, `database`, `cloud`, `general-concept`.
   - Append any extra top-level directories at the repo root that are not dot-folders, not files, and not one of the six above. Sort the extras alphabetically.
2. For each category folder that exists on disk, list its immediate subdirectories sorted alphabetically. Each subdirectory is a topic slug. If the category folder does not exist, treat its topic list as empty (still render the section).
3. Overwrite `<repo root>/TOPICS.md` with this exact shape:

```
# Topics in this repo

This file is auto-generated by `/learn*` commands after the `topic-folder-manager` skill runs. Do not edit by hand — changes will be overwritten on the next run.

## <category> (<N>)
- [`<slug>`](<category>/<slug>/)
- ...

(repeat for every category in the list from step 1, in that order)

---

**<total> topics across <non-empty-category-count> categories.**
```

4. If a category has zero topics, render the line `_none yet_` on its own line under the heading instead of a list.
5. The trailing footer counts only categories that have at least one topic.

This rebuild runs on **every** invocation, even when `STATUS` is `existed`. The cost is one filesystem scan and a small file write; the guarantee is that the index never drifts.

## What you return

When invoked, you produce a short structured response in this exact form (whether the folder was new or pre-existing):

```
TOPIC: <human-readable topic name>
CATEGORY: <one of: frontend | backend | ai | database | cloud | general-concept | <existing-extra-dir>>
SLUG: <slug>
PATH: <category>/<slug>
ABSOLUTE_PATH: <absolute path>
STATUS: <created | existed>
CLASSIFICATION_BASIS: <name-based | web-search | existing-folder>
NEXT_STEP: <docs/01-overview.md | docs/02-deep-dive.md | docs/03-practice.md | code/mvp.* | complete>
```

`CLASSIFICATION_BASIS` records how the decision was reached so the orchestrating command knows whether a web search was burned for this topic. If the existing-folder override fired in Step 5, set it to `existing-folder`. If Step 1 settled the answer with no search, set it to `name-based`. Otherwise, `web-search`.

`NEXT_STEP` should reflect the **first missing artifact** in the standard pipeline so the orchestrating command knows where to resume:
- If `docs/01-overview.md` is missing → `docs/01-overview.md`
- Else if `docs/02-deep-dive.md` is missing → `docs/02-deep-dive.md`
- Else if `docs/03-practice.md` is missing → `docs/03-practice.md`
- Else if no file matching `code/mvp.*` exists → `code/mvp.*`
- Else → `complete`

That is the entire output of this skill. No commentary, no narration.
