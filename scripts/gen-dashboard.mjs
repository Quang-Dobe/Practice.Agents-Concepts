#!/usr/bin/env node
// Regenerate the root dashboard (index.html) from the filesystem.
//
// Scans every <category>/<topic>/present/index.html, pulls each topic's title +
// one-line hook, and rewrites index.html so newly-added topics appear
// automatically. Run after adding a topic's present/ pages (the /learn present
// stage does this), or any time by hand:  node scripts/gen-dashboard.mjs
//
// No dependencies — plain Node ≥ 18.

import { readFileSync, writeFileSync, readdirSync, existsSync, statSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO = resolve(dirname(fileURLToPath(import.meta.url)), '..');

// Known categories: display name + accent, in dashboard order. Extra top-level
// category folders are appended after these with a neutral accent.
const CATS = {
  frontend:          { name: 'Frontend',          accent: '#58a6ff' },
  backend:           { name: 'Backend',           accent: '#3fb950' },
  ai:                { name: 'AI',                 accent: '#a371f7' },
  database:          { name: 'Database',           accent: '#d29922' },
  cloud:             { name: 'Cloud',              accent: '#ec6547' },
  'general-concept': { name: 'General Concepts',   accent: '#ec6cb9' },
};
const ORDER = ['frontend', 'backend', 'ai', 'database', 'cloud', 'general-concept'];
const NEUTRAL = '#6ea8fe';
const SKIP = new Set(['.git', '.github', '.claude', 'assets', 'scripts', 'node_modules']);

const decode = (s) => s
  .replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>')
  .replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&nbsp;/g, ' ').trim();
const esc = (s) => s
  .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
const titleCase = (slug) => slug.split('-').map(w => w ? w[0].toUpperCase() + w.slice(1) : w).join(' ');

function isDir(p) { try { return statSync(p).isDirectory(); } catch { return false; } }

// Extract {title, hook} for one topic. Prefer the present page's hero (what the
// reader sees); fall back to docs/01-overview.md.
function topicMeta(catDir, slug) {
  const base = resolve(REPO, catDir, slug);
  const present = resolve(base, 'present', 'index.html');
  let title = '', hook = '';
  if (existsSync(present)) {
    const html = readFileSync(present, 'utf8');
    const t = html.match(/class="hero__title"[^>]*>([\s\S]*?)<\/h1>/i);
    const g = html.match(/class="hero__tagline"[^>]*>([\s\S]*?)<\/p>/i);
    if (t) title = decode(t[1].replace(/<[^>]+>/g, ''));
    if (g) hook = decode(g[1].replace(/<[^>]+>/g, ''));
  }
  const overview = resolve(base, 'docs', '01-overview.md');
  if ((!title || !hook) && existsSync(overview)) {
    const md = readFileSync(overview, 'utf8');
    if (!title) {
      const h1 = md.match(/^#\s+(.+)$/m);
      if (h1) title = h1[1].replace(/\s+[—-]\s*Overview\s*$/i, '').trim();
    }
    if (!hook) {
      const bq = md.match(/^>\s+(.+)$/m);
      if (bq) hook = bq[1].trim();
    }
  }
  if (!title) title = titleCase(slug);
  if (!hook) hook = '';
  return { title, hook };
}

// Discover categories → their topics (only topics that have present/index.html).
const rootEntries = readdirSync(REPO).filter(n => !SKIP.has(n) && !n.startsWith('.') && isDir(resolve(REPO, n)));
const cats = [];
const ordered = [...ORDER.filter(c => rootEntries.includes(c)), ...rootEntries.filter(c => !ORDER.includes(c)).sort()];
for (const cat of ordered) {
  const topics = readdirSync(resolve(REPO, cat))
    .filter(slug => isDir(resolve(REPO, cat, slug)) && existsSync(resolve(REPO, cat, slug, 'present', 'index.html')))
    .sort()
    .map(slug => ({ slug, ...topicMeta(cat, slug) }));
  if (topics.length) cats.push({ cat, meta: CATS[cat] || { name: titleCase(cat), accent: NEUTRAL }, topics });
}

const totalTopics = cats.reduce((n, c) => n + c.topics.length, 0);

function card(cat, t) {
  const search = esc(`${t.title} ${t.slug.replace(/-/g, ' ')} ${cat}`.toLowerCase());
  return `        <a class="topic-card" data-cat="${cat}" data-search="${search}" href="${cat}/${t.slug}/present/index.html">
          <span class="topic-card__cat">${cat}</span>
          <h3 class="topic-card__title">${esc(t.title)}</h3>
          <p class="topic-card__desc">${esc(t.hook)}</p>
        </a>`;
}

function group(c) {
  return `    <section class="dash-cat-group">
      <h2 class="dash-cat-group__head"><span style="color:${c.meta.accent}">${esc(c.meta.name)}</span> <span class="badge">${c.topics.length}</span></h2>
      <div class="dash-grid">
${c.topics.map(t => card(c.cat, t)).join('\n')}
      </div>
    </section>`;
}

function chip(c) {
  return `        <button class="filter-chip" type="button" data-cat="${c.cat}" aria-pressed="false">${esc(c.meta.name)}</button>`;
}

const html = `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Learning Notebook — ${totalTopics} Software-Engineering Topics</title>
  <meta name="description" content="Intuition-first notes on ${totalTopics} software-engineering topics: overview, deep dive, and real-world practice for each, with diagrams.">
  <link rel="icon" type="image/svg+xml" href="./assets/favicon.svg">
  <link rel="stylesheet" href="./assets/site.css">
</head>
<body style="--accent:#6ea8fe; --accent-soft:rgba(110,168,254,0.15)">

  <a class="skip-link" href="#main">Skip to content</a>
  <div class="progress-bar" role="presentation"></div>

  <main id="main" class="dash">

    <header class="dash__header">
      <div class="dash__top">
        <div>
          <h1 class="dash__title">Learning Notebook</h1>
          <p class="dash__subtitle">Intuition-first notes on ${totalTopics} software-engineering topics — each with an overview, a deep dive, and real-world practice, explained with diagrams.</p>
        </div>
        <button class="theme-toggle" type="button" aria-label="Toggle light or dark theme">
          <span class="theme-toggle__moon" aria-hidden="true">☾</span>
          <span class="theme-toggle__sun" aria-hidden="true">☀</span>
          <span>Theme</span>
        </button>
      </div>

      <div class="stat-grid dash__stats">
        <div class="stat"><div class="stat__num">${totalTopics}</div><div class="stat__label">topics</div></div>
        <div class="stat"><div class="stat__num">${cats.length}</div><div class="stat__label">categories</div></div>
        <div class="stat"><div class="stat__num">3</div><div class="stat__label">docs per topic</div></div>
        <div class="stat"><div class="stat__num">${totalTopics * 4}</div><div class="stat__label">pages</div></div>
      </div>
    </header>

    <div class="dash-controls">
      <input class="dash-search" type="search" placeholder="Search topics — e.g. cache, retry, index…" aria-label="Search topics">
      <div class="dash-filters" role="group" aria-label="Filter by category">
        <button class="filter-chip is-active" type="button" data-cat="all" aria-pressed="true">All</button>
${cats.map(chip).join('\n')}
      </div>
    </div>

    <p class="dash-empty">No topics match your search.</p>

${cats.map(group).join('\n\n')}

    <footer class="dash__footer">
      <p>A personal tech-learning notebook. Each topic also keeps its full written docs and runnable code in the repository.</p>
      <p><a href="https://github.com/Quang-Dobe/Practice.Agents-Concepts">View the repository on GitHub ↗</a></p>
    </footer>

  </main>

  <script src="./assets/site.js"></script>
</body>
</html>
`;

writeFileSync(resolve(REPO, 'index.html'), html);
console.log(`Dashboard regenerated: ${totalTopics} topics across ${cats.length} categories.`);
cats.forEach(c => console.log(`  ${c.cat}: ${c.topics.length}`));
