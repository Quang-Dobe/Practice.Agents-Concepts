/* =====================================================================
   site.js — shared behavior for the presentation site. Vanilla, no deps.
   Responsibilities:
     1. Theme (dark default, persisted in localStorage) — runs FIRST to
        avoid a flash of the wrong theme.
     2. Mermaid initialization + render (theme-matched to our palette).
     3. Sidebar / hamburger drawer toggle (mobile).
     4. Active-nav detection based on the current filename.
     5. Reading-progress bar.
     6. Dashboard search + category filter (guarded; no-ops elsewhere).
   Every block is guarded so a page missing an element never throws.

   MERMAID ORDERING NOTE (important for implementers):
   In the HTML, load mermaid.min.js WITHOUT `defer`, then site.js AFTER it
   (also without defer), both right before </body>. We call
   mermaid.initialize() immediately and mermaid.run() on DOMContentLoaded,
   so rendering is deterministic regardless of parse timing. We deliberately
   set startOnLoad=false here and drive run() ourselves.
   ===================================================================== */
(function () {
  "use strict";

  /* ---- 1. THEME (must run before paint) --------------------------- */
  var THEME_KEY = "present-theme";
  function applyTheme(theme) {
    document.documentElement.setAttribute("data-theme", theme);
  }
  function initTheme() {
    var saved;
    try { saved = localStorage.getItem(THEME_KEY); } catch (e) { saved = null; }
    applyTheme(saved === "light" ? "light" : "dark"); // dark is the default
  }
  function toggleTheme() {
    var current = document.documentElement.getAttribute("data-theme");
    var next = current === "light" ? "dark" : "light";
    applyTheme(next);
    try { localStorage.setItem(THEME_KEY, next); } catch (e) {}
    renderMermaid(); // re-render diagrams so their theme matches
  }
  initTheme(); // synchronous, before DOMContentLoaded

  /* ---- 1b. SIDEBAR COLLAPSE STATE (must also run before paint) ---- */
  /* Applied on <html> so the CSS grid re-flows before first paint. */
  var COLLAPSE_KEY = "present-sidebar-collapsed";
  function readCollapsed() {
    var v;
    try { v = localStorage.getItem(COLLAPSE_KEY); } catch (e) { v = null; }
    // Default = collapsed on first visit.
    return v === null ? true : v === "1";
  }
  function applyCollapsed(collapsed) {
    document.documentElement.classList.toggle("sidebar-collapsed", collapsed);
  }
  applyCollapsed(readCollapsed());

  /* ---- 2. MERMAID ------------------------------------------------- */
  var mermaidReady = false;
  function mermaidThemeVars() {
    // Read the live accent so diagrams pick up the page's category color.
    var styles = getComputedStyle(document.body || document.documentElement);
    var accent = (styles.getPropertyValue("--accent") || "#3fb950").trim();
    var isLight = document.documentElement.getAttribute("data-theme") === "light";
    var bg     = isLight ? "#f6f8fa" : "#161b22";
    var bg2    = isLight ? "#eef1f5" : "#1c2129";
    var text   = isLight ? "#1f2328" : "#e6edf3";
    var line   = isLight ? "#59636e" : "#8b949e";
    var border = isLight ? "#d0d7de" : "#30363d";
    return {
      accent: accent, isLight: isLight,
      vars: {
        darkMode: !isLight,
        background: bg,
        primaryColor: bg2,
        primaryBorderColor: accent,
        primaryTextColor: text,
        secondaryColor: bg,
        tertiaryColor: bg,
        lineColor: line,
        textColor: text,
        mainBkg: bg2,
        nodeBorder: accent,
        clusterBkg: bg,
        clusterBorder: border,
        edgeLabelBackground: bg,
        fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif',
        fontSize: "14px"
      }
    };
  }
  function initMermaid() {
    if (typeof window.mermaid === "undefined") return;
    var t = mermaidThemeVars();
    try {
      window.mermaid.initialize({
        startOnLoad: false,
        theme: t.isLight ? "default" : "dark",
        securityLevel: "loose",
        fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif',
        themeVariables: t.vars
      });
      mermaidReady = true;
    } catch (e) { /* mermaid missing/broken — leave source visible */ }
  }
  function renderMermaid() {
    if (typeof window.mermaid === "undefined") return;
    initMermaid(); // re-init so a theme toggle re-themes diagrams
    var nodes = document.querySelectorAll(".mermaid");
    if (!nodes.length) return;
    // Restore original source before re-rendering (needed on theme toggle).
    nodes.forEach(function (n) {
      if (n.getAttribute("data-processed") === "true" && n.dataset.src) {
        n.removeAttribute("data-processed");
        n.innerHTML = n.dataset.src;
      } else if (!n.dataset.src) {
        n.dataset.src = n.innerHTML;
      }
    });
    try {
      if (typeof window.mermaid.run === "function") {
        window.mermaid.run({ nodes: nodes });
      } else if (typeof window.mermaid.init === "function") {
        window.mermaid.init(undefined, nodes); // legacy API fallback
      }
    } catch (e) { /* diagram syntax error — source stays visible, page fine */ }
  }

  /* ---- 3. SIDEBAR / HAMBURGER ------------------------------------- */
  function initSidebar() {
    var sidebar = document.querySelector(".sidebar");
    var btn = document.querySelector(".hamburger");
    var scrim = document.querySelector(".scrim");
    if (!sidebar || !btn) return;

    function open()  { sidebar.classList.add("is-open");  btn.setAttribute("aria-expanded", "true");  if (scrim) scrim.classList.add("is-visible"); }
    function close() { sidebar.classList.remove("is-open"); btn.setAttribute("aria-expanded", "false"); if (scrim) scrim.classList.remove("is-visible"); }
    function toggle() { sidebar.classList.contains("is-open") ? close() : open(); }

    btn.addEventListener("click", toggle);
    if (scrim) scrim.addEventListener("click", close);
    // close the drawer after tapping a nav link
    sidebar.querySelectorAll("a").forEach(function (a) {
      a.addEventListener("click", function () { if (window.innerWidth <= 860) close(); });
    });
    // Esc closes it
    document.addEventListener("keydown", function (e) {
      if (e.key === "Escape") close();
    });
  }

  /* ---- 3b. SIDEBAR COLLAPSE (desktop) ------------------------------ */
  function initCollapse() {
    var sidebar = document.querySelector(".sidebar");
    if (!sidebar) return;

    // Add a collapse button into the sidebar header (absolute-positioned in CSS).
    var header = sidebar.querySelector(".sidebar__header");
    var btnCollapse = null;
    if (header && !header.querySelector(".sidebar__collapse")) {
      btnCollapse = document.createElement("button");
      btnCollapse.type = "button";
      btnCollapse.className = "sidebar__collapse";
      btnCollapse.setAttribute("aria-label", "Collapse sidebar");
      btnCollapse.setAttribute("aria-controls", "sidebar");
      btnCollapse.setAttribute("title", "Collapse sidebar");
      btnCollapse.textContent = "◀"; // ◀
      header.appendChild(btnCollapse);
    } else if (header) {
      btnCollapse = header.querySelector(".sidebar__collapse");
    }

    // Floating expand button (visible only when sidebar is collapsed).
    var btnExpand = document.querySelector(".sidebar-expand");
    if (!btnExpand) {
      btnExpand = document.createElement("button");
      btnExpand.type = "button";
      btnExpand.className = "sidebar-expand";
      btnExpand.setAttribute("aria-label", "Expand sidebar");
      btnExpand.setAttribute("aria-controls", "sidebar");
      btnExpand.setAttribute("title", "Expand sidebar");
      btnExpand.textContent = "☰"; // ☰
      document.body.appendChild(btnExpand);
    }

    function sync() {
      var collapsed = document.documentElement.classList.contains("sidebar-collapsed");
      if (btnCollapse) btnCollapse.setAttribute("aria-expanded", (!collapsed).toString());
      btnExpand.setAttribute("aria-expanded", (!collapsed).toString());
    }
    function set(collapsed) {
      applyCollapsed(collapsed);
      try { localStorage.setItem(COLLAPSE_KEY, collapsed ? "1" : "0"); } catch (e) {}
      sync();
    }

    if (btnCollapse) btnCollapse.addEventListener("click", function () { set(true); });
    btnExpand.addEventListener("click", function () { set(false); });
    sync();
  }

  /* ---- 4. ACTIVE NAV (by filename) -------------------------------- */
  function initActiveNav() {
    var here = window.location.pathname.split("/").pop() || "index.html";
    if (here === "") here = "index.html";
    document.querySelectorAll(".sidebar__link").forEach(function (link) {
      var href = (link.getAttribute("href") || "").split("/").pop();
      // mark active if filename matches; treat "" / "./" as index.html
      var target = href === "" || href === "./" ? "index.html" : href;
      if (target === here) {
        link.classList.add("is-active");
        link.setAttribute("aria-current", "page");
      }
    });
  }

  /* ---- 5. READING-PROGRESS BAR ------------------------------------ */
  function initProgress() {
    var bar = document.querySelector(".progress-bar");
    if (!bar) return;
    var ticking = false;
    function update() {
      var doc = document.documentElement;
      var scrollable = doc.scrollHeight - doc.clientHeight;
      var pct = scrollable > 0 ? (doc.scrollTop / scrollable) * 100 : 0;
      bar.style.setProperty("--progress", pct.toFixed(2) + "%");
      ticking = false;
    }
    function onScroll() {
      if (!ticking) { window.requestAnimationFrame(update); ticking = true; }
    }
    window.addEventListener("scroll", onScroll, { passive: true });
    window.addEventListener("resize", onScroll, { passive: true });
    update();
  }

  /* ---- 6. THEME TOGGLE BUTTON WIRING ------------------------------ */
  function initThemeToggle() {
    var btn = document.querySelector(".theme-toggle");
    if (!btn) return;
    btn.addEventListener("click", toggleTheme);
  }

  /* ---- 7. DASHBOARD SEARCH + FILTER (guarded) --------------------- */
  function initDashboard() {
    var search = document.querySelector(".dash-search");
    var cards = document.querySelectorAll(".topic-card");
    if (!cards.length) return; // not the dashboard — no-op
    var chips = document.querySelectorAll(".filter-chip");
    var empty = document.querySelector(".dash-empty");
    var groups = document.querySelectorAll(".dash-cat-group");
    var activeCat = "all";

    function apply() {
      var q = (search && search.value ? search.value : "").trim().toLowerCase();
      var shown = 0;
      cards.forEach(function (card) {
        var cat = card.getAttribute("data-cat") || "";
        var text = (card.getAttribute("data-search") || card.textContent || "").toLowerCase();
        var matchCat = activeCat === "all" || cat === activeCat;
        var matchText = q === "" || text.indexOf(q) !== -1;
        var show = matchCat && matchText;
        card.classList.toggle("topic-card__hidden", !show);
        if (show) shown++;
      });
      // hide a category group header if all of its cards are filtered out
      groups.forEach(function (g) {
        var visible = g.querySelectorAll(".topic-card:not(.topic-card__hidden)").length;
        g.style.display = visible ? "" : "none";
      });
      if (empty) empty.classList.toggle("is-visible", shown === 0);
    }

    if (search) search.addEventListener("input", apply);
    chips.forEach(function (chip) {
      chip.addEventListener("click", function () {
        chips.forEach(function (c) { c.classList.remove("is-active"); c.setAttribute("aria-pressed", "false"); });
        chip.classList.add("is-active");
        chip.setAttribute("aria-pressed", "true");
        activeCat = chip.getAttribute("data-cat") || "all";
        apply();
      });
    });
    apply();
  }

  /* ---- BOOT ------------------------------------------------------- */
  // Mermaid may already be parsed (script precedes us); init now if present.
  initMermaid();
  function boot() {
    initSidebar();
    initCollapse();
    initActiveNav();
    initProgress();
    initThemeToggle();
    initDashboard();
    renderMermaid();
  }
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", boot);
  } else {
    boot();
  }
})();
