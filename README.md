# Learning Notebook

### 🌐 Live site — **https://quang-dobe.github.io/Practice.Agents-Concepts/**

A personal tech-learning notebook. **25 software-engineering topics**, each explained three ways — a plain-English **overview**, a **deep dive**, and **real-world practice** — plus a small runnable code demo. The live site above turns every topic into a clean, dark-themed, diagram-first web page you can read in a browser.

> New here? Open the [live dashboard](https://quang-dobe.github.io/Practice.Agents-Concepts/), pick any topic, and read. Each topic page has a sidebar to jump between Overview → Detail → Practice, and a **← All topics** button to come back.

---

## What's inside

Topics are grouped into **6 categories**:

| Category | Topics |
|---|---|
| **Frontend** | hydration · service-worker · virtual-dom · web-components |
| **Backend** | circuit-breaker · connection-pooling · grpc · jwt · rate-limiting |
| **AI** | attention-mechanism · embeddings · harness-engineer · tokenization |
| **Database** | b-tree-index · lsm-tree · mvcc · sharding |
| **Cloud** | blue-green-deployment · cdn-edge-caching · service-mesh · sidecar-pattern |
| **General concepts** | backpressure · cap-theorem · event-sourcing · idempotency |

## How each topic is organized

```
<category>/<topic>/
├── docs/            # the source notes (Markdown)
│   ├── 01-overview.md    # easy, intuition-first explanation
│   ├── 02-deep-dive.md   # What / Where / When / How / Why
│   └── 03-practice.md    # best practices + anti-patterns
├── code/            # a minimal runnable demo + README
└── present/         # the web pages (this is what the live site shows)
    ├── index.html        # topic landing + a hero diagram
    ├── overview.html
    ├── detail.html
    └── practice.html
```

The `present/` pages are **re-authored from the docs** to be easy to skim: short words, bulleted lists, small *italic* explanations of jargon, and a **diagram on every page** (state machines, request flows, and hand-drawn concept art). The written `docs/` and runnable `code/` stay as the source of truth.

## How topics are added

Topics are produced by the `/learn` pipeline (`.claude/`), which runs five stages: overview → deep dive → practice → MVP code → **present pages**. The final stage (`present-builder`) re-authors the docs into the `present/` HTML using the shared `present-page-conventions` skill, then regenerates this dashboard with `scripts/gen-dashboard.mjs` so the new topic appears automatically. A weekly Claude Routine runs the pipeline on a fresh topic and opens a PR; merging it redeploys the site.

To rebuild the dashboard by hand at any time:

```bash
node scripts/gen-dashboard.mjs
```

## Run the site locally

No build step — it's plain static HTML/CSS/JS. Serve the repo root and open the dashboard:

```bash
# any static server works; for example:
npx http-server -p 8099 .
# then open http://localhost:8099/
```

## How it deploys

A GitHub Actions workflow (`.github/workflows/deploy-pages.yml`) publishes the whole site to GitHub Pages on every push to `main`.

**One-time setup** (in the repo UI): **Settings → Pages → Build and deployment → Source: _GitHub Actions_.** After that, the live URL at the top updates automatically on each push to `main`.
