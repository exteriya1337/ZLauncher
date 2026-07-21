/**
 * ZLauncher landing — counters, download, animations
 */

// ── Ссылка на релиз (поменяй на свою) ──
const DOWNLOAD_URL =
  "https://github.com/exteriya1337/ZLauncher/releases/latest/download/ZLauncher.Setup.exe";

const COUNTER_NS = "zlauncher-site";
const COUNTER_VISITS = "visits";
const COUNTER_DOWNLOADS = "downloads";

const LS_VISITS = "zl_visits_local";
const LS_DOWNLOADS = "zl_downloads_local";
const LS_SESSION = "zl_visit_session";

const reduceMotion =
  typeof window !== "undefined" &&
  window.matchMedia &&
  window.matchMedia("(prefers-reduced-motion: reduce)").matches;

function formatCount(n) {
  const num = Number(n);
  if (!Number.isFinite(num) || num < 0) return "—";
  return new Intl.NumberFormat("ru-RU").format(Math.floor(num));
}

function popStat(el) {
  if (!el || reduceMotion) return;
  el.classList.remove("is-ticking");
  // restart CSS animation
  void el.offsetWidth;
  el.classList.add("is-ticking");
  window.setTimeout(() => el.classList.remove("is-ticking"), 400);
}

function animateCount(el, to, { duration = 700 } = {}) {
  if (!el) return;

  const target = Math.floor(Number(to));
  if (!Number.isFinite(target) || target < 0) {
    el.textContent = "—";
    return;
  }

  if (reduceMotion) {
    el.textContent = formatCount(target);
    return;
  }

  const fromRaw = parseInt(String(el.textContent).replace(/\s/g, ""), 10);
  const from = Number.isFinite(fromRaw) ? fromRaw : 0;
  if (from === target) {
    el.textContent = formatCount(target);
    popStat(el);
    return;
  }

  const start = performance.now();
  const delta = target - from;

  function frame(now) {
    const t = Math.min(1, (now - start) / duration);
    // ease-out cubic
    const eased = 1 - Math.pow(1 - t, 3);
    const value = Math.round(from + delta * eased);
    el.textContent = formatCount(value);
    if (t < 1) {
      requestAnimationFrame(frame);
    } else {
      el.textContent = formatCount(target);
      popStat(el);
    }
  }

  requestAnimationFrame(frame);
}

function setCountUI(visitor, downloads, { animate = false } = {}) {
  const vc = document.getElementById("visitor-count");
  const dc = document.getElementById("download-count");

  if (animate) {
    animateCount(vc, visitor);
    animateCount(dc, downloads);
  } else {
    if (vc) vc.textContent = formatCount(visitor);
    if (dc) dc.textContent = formatCount(downloads);
  }
}

function readLocal(key, fallback = 0) {
  try {
    const n = parseInt(localStorage.getItem(key), 10);
    return Number.isFinite(n) && n >= 0 ? n : fallback;
  } catch {
    return fallback;
  }
}

function writeLocal(key, value) {
  try {
    localStorage.setItem(key, String(value));
  } catch {
    /* ignore */
  }
}

async function counterHit(key, { increment = false } = {}) {
  const action = increment ? "up" : "";
  const url = `https://api.counterapi.dev/v1/${encodeURIComponent(COUNTER_NS)}/${encodeURIComponent(key)}/${action}`;
  const res = await fetch(url, { method: "GET", cache: "no-store" });
  if (!res.ok) throw new Error(`counter ${res.status}`);
  const data = await res.json();
  const count = data?.count ?? data?.value;
  if (typeof count !== "number") throw new Error("bad counter payload");
  return count;
}

async function initVisitors() {
  let alreadyCounted = false;
  try {
    alreadyCounted = sessionStorage.getItem(LS_SESSION) === "1";
  } catch {
    /* ignore */
  }

  let localVisits = readLocal(LS_VISITS, 0);

  try {
    const count = alreadyCounted
      ? await counterHit(COUNTER_VISITS, { increment: false })
      : await counterHit(COUNTER_VISITS, { increment: true });

    if (!alreadyCounted) {
      try {
        sessionStorage.setItem(LS_SESSION, "1");
      } catch {
        /* ignore */
      }
      localVisits = Math.max(localVisits + 1, count);
      writeLocal(LS_VISITS, localVisits);
    } else {
      localVisits = Math.max(localVisits, count);
      writeLocal(LS_VISITS, localVisits);
    }
    return count;
  } catch {
    if (!alreadyCounted) {
      localVisits += 1;
      writeLocal(LS_VISITS, localVisits);
      try {
        sessionStorage.setItem(LS_SESSION, "1");
      } catch {
        /* ignore */
      }
    }
    return localVisits;
  }
}

async function initDownloadsDisplay() {
  const local = readLocal(LS_DOWNLOADS, 0);
  try {
    const count = await counterHit(COUNTER_DOWNLOADS, { increment: false });
    writeLocal(LS_DOWNLOADS, Math.max(local, count));
    return count;
  } catch {
    return local;
  }
}

async function registerDownloadClick() {
  const local = readLocal(LS_DOWNLOADS, 0) + 1;
  writeLocal(LS_DOWNLOADS, local);
  setCountUI(readLocal(LS_VISITS, 0), local, { animate: true });

  try {
    const count = await counterHit(COUNTER_DOWNLOADS, { increment: true });
    writeLocal(LS_DOWNLOADS, Math.max(local, count));
    setCountUI(readLocal(LS_VISITS, 0), count, { animate: true });
  } catch {
    /* local already shown */
  }
}

function wireDownload() {
  const btn = document.getElementById("download-btn");
  if (!btn) return;
  btn.setAttribute("href", DOWNLOAD_URL);
  btn.addEventListener("click", () => {
    registerDownloadClick();
  });
}

function startEntrance() {
  // next frame so CSS transitions apply after paint
  requestAnimationFrame(() => {
    document.body.classList.remove("is-loading");
    document.body.classList.add("is-ready");
  });
}

// ── Site tabs: Главная / Changelog ───────────────────────────
const GH_RELEASES_URL =
  "https://api.github.com/repos/exteriya1337/ZLauncher/releases?per_page=20";
const GH_RELEASES_PAGE =
  "https://github.com/exteriya1337/ZLauncher/releases";
const LS_CHANGELOG = "zl_changelog_cache_v1";
const CHANGELOG_TTL_MS = 30 * 60 * 1000; // 30 мин — экономим rate limit

let changelogLoaded = false;
let changelogLoading = false;
let currentSiteTab = "home";
let tabAnimating = false;

const TAB_OUT_MS = 240;
const TAB_IN_MS = 340;

function waitMs(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function updateTabButtons(tab) {
  document.querySelectorAll(".site-tab").forEach((t) => {
    const on = t.getAttribute("data-tab") === tab;
    t.classList.toggle("is-active", on);
    t.setAttribute("aria-selected", on ? "true" : "false");
  });
}

function updateTabHash(tab) {
  if (tab === "changelog") {
    if (location.hash !== "#changelog") {
      history.replaceState(null, "", "#changelog");
    }
  } else if (location.hash === "#changelog" || location.hash === "#home") {
    history.replaceState(null, "", "#home");
  }
}

function switchPanelsInstant(from, to) {
  if (from && from !== to) {
    from.classList.remove("is-active", "is-exit", "is-enter");
    from.setAttribute("hidden", "");
    from.style.removeProperty("--tab-dir");
  }
  if (to) {
    to.removeAttribute("hidden");
    to.classList.remove("is-exit", "is-enter");
    to.classList.add("is-active");
    to.style.removeProperty("--tab-dir");
  }
}

async function setActiveTab(tab, { force = false } = {}) {
  if (!tab) return;
  if (!force && (tab === currentSiteTab || tabAnimating)) return;

  const to = document.querySelector(`.site-panel[data-panel="${tab}"]`);
  if (!to) return;

  const from = document.querySelector(".site-panel.is-active");
  // направление: changelog правее, home левее
  const dir = tab === "changelog" ? 1 : -1;

  updateTabButtons(tab);

  if (reduceMotion || !from || from === to) {
    switchPanelsInstant(from, to);
    currentSiteTab = tab;
    updateTabHash(tab);
    if (tab === "changelog") loadChangelog();
    window.scrollTo({ top: 0, behavior: "auto" });
    return;
  }

  tabAnimating = true;

  // 1) уход текущей панели
  from.style.setProperty("--tab-dir", String(dir));
  from.classList.add("is-exit");
  from.classList.remove("is-active");

  await waitMs(TAB_OUT_MS);

  from.setAttribute("hidden", "");
  from.classList.remove("is-exit");
  from.style.removeProperty("--tab-dir");

  // 2) появление новой
  to.style.setProperty("--tab-dir", String(dir));
  to.removeAttribute("hidden");
  to.classList.add("is-enter");
  // reflow, чтобы анимация стартовала
  void to.offsetWidth;
  to.classList.add("is-active");

  await waitMs(TAB_IN_MS);

  to.classList.remove("is-enter");
  to.style.removeProperty("--tab-dir");

  currentSiteTab = tab;
  tabAnimating = false;
  updateTabHash(tab);

  if (tab === "changelog") loadChangelog();

  window.scrollTo({
    top: 0,
    behavior: reduceMotion ? "auto" : "smooth",
  });
}

function wireTabs() {
  document.querySelectorAll("[data-tab]").forEach((el) => {
    el.addEventListener("click", (e) => {
      e.preventDefault();
      const tab = el.getAttribute("data-tab");
      if (tab) setActiveTab(tab);
    });
  });

  if (location.hash === "#changelog") {
    // без анимации на первом заходе по прямой ссылке
    setActiveTab("changelog", { force: true });
  } else {
    currentSiteTab = "home";
  }
}

function escapeHtml(s) {
  return String(s)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}

/** Inline markdown (после escapeHtml) */
function applyInlineMd(text) {
  // links [text](url)
  text = text.replace(
    /\[([^\]]+)\]\((https?:\/\/[^)\s]+)\)/g,
    '<a href="$2" target="_blank" rel="noopener noreferrer">$1</a>'
  );
  // bare urls (не внутри уже созданных href)
  text = text.replace(
    /(^|[\s(>])(https?:\/\/[^\s<]+)/g,
    '$1<a href="$2" target="_blank" rel="noopener noreferrer">$2</a>'
  );
  // **bold**
  text = text.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
  // `code`
  text = text.replace(/`([^`]+)`/g, "<code>$1</code>");
  return text;
}

function applyLineMd(line) {
  let t = line;
  t = t.replace(/^### (.+)$/, "<strong>$1</strong>");
  t = t.replace(/^## (.+)$/, "<strong>$1</strong>");
  t = t.replace(/^# (.+)$/, "<strong>$1</strong>");
  t = t.replace(/^[-*] (.+)$/, "• $1");
  return t;
}

function isTableRowLine(line) {
  const t = line.trim();
  if (!t.includes("|")) return false;
  // хотя бы одна ячейка между |
  return /^\|?.+\|.+\|?$/.test(t);
}

function isTableSeparatorLine(line) {
  const t = line.trim();
  // | --- | :---: | ---: |
  if (!t.includes("|") || !t.includes("-")) return false;
  const cells = t
    .replace(/^\|/, "")
    .replace(/\|$/, "")
    .split("|")
    .map((c) => c.trim());
  if (cells.length < 1) return false;
  return cells.every((c) => /^:?-{1,}:?$/.test(c));
}

function parseTableAlign(sepLine) {
  return sepLine
    .trim()
    .replace(/^\|/, "")
    .replace(/\|$/, "")
    .split("|")
    .map((c) => {
      const s = c.trim();
      const left = s.startsWith(":");
      const right = s.endsWith(":");
      if (left && right) return "center";
      if (right) return "right";
      return "left";
    });
}

function splitTableCells(line) {
  let t = line.trim();
  if (t.startsWith("|")) t = t.slice(1);
  if (t.endsWith("|")) t = t.slice(0, -1);
  return t.split("|").map((c) => c.trim());
}

function looksLikeTableStart(lines, i) {
  if (i + 1 >= lines.length) return false;
  return isTableRowLine(lines[i]) && isTableSeparatorLine(lines[i + 1]);
}

function parseMarkdownTable(lines, start) {
  const headerCells = splitTableCells(lines[start]);
  const aligns = parseTableAlign(lines[start + 1]);
  const rows = [];
  let i = start + 2;
  while (i < lines.length && isTableRowLine(lines[i]) && !isTableSeparatorLine(lines[i])) {
    rows.push(splitTableCells(lines[i]));
    i++;
  }

  const alignFor = (idx) => aligns[idx] || "left";

  let html = '<div class="cl-table-wrap"><table class="cl-table"><thead><tr>';
  headerCells.forEach((cell, idx) => {
    html += `<th style="text-align:${alignFor(idx)}">${applyInlineMd(escapeHtml(cell))}</th>`;
  });
  html += "</tr></thead><tbody>";

  rows.forEach((row) => {
    html += "<tr>";
    // выровнять число ячеек под заголовок
    for (let c = 0; c < headerCells.length; c++) {
      const cell = row[c] != null ? row[c] : "";
      html += `<td style="text-align:${alignFor(c)}">${applyInlineMd(escapeHtml(cell))}</td>`;
    }
    html += "</tr>";
  });

  html += "</tbody></table></div>";
  return { html, next: i };
}

/**
 * Markdown → HTML: таблицы GFM, заголовки, bold, code, ссылки, списки.
 */
function simpleMarkdown(md) {
  if (!md || !md.trim()) return "";

  const lines = md.replace(/\r\n/g, "\n").split("\n");
  const parts = [];
  let textBuf = [];
  let i = 0;

  function flushText() {
    if (!textBuf.length) return;
    const chunk = textBuf.join("\n");
    textBuf = [];
    if (!chunk.trim()) {
      parts.push("<br>");
      return;
    }
    let t = escapeHtml(chunk);
    t = applyInlineMd(t);
    t = t
      .split("\n")
      .map((line) => applyLineMd(line))
      .join("<br>\n");
    parts.push(`<div class="cl-md">${t}</div>`);
  }

  while (i < lines.length) {
    if (looksLikeTableStart(lines, i)) {
      flushText();
      const { html, next } = parseMarkdownTable(lines, i);
      parts.push(html);
      i = next;
      continue;
    }
    textBuf.push(lines[i]);
    i++;
  }
  flushText();

  return parts.join("\n");
}

function formatReleaseDate(iso) {
  try {
    const d = new Date(iso);
    return new Intl.DateTimeFormat("ru-RU", {
      day: "numeric",
      month: "long",
      year: "numeric",
    }).format(d);
  } catch {
    return "";
  }
}

function renderReleases(releases) {
  const list = document.getElementById("changelog-list");
  const status = document.getElementById("changelog-status");
  if (!list) return;

  if (!releases || releases.length === 0) {
    if (status) {
      status.className = "changelog-status";
      status.textContent = "Релизов пока нет.";
    }
    list.innerHTML = "";
    return;
  }

  if (status) {
    status.className = "changelog-status is-ok";
    status.textContent = "";
  }

  list.innerHTML = releases
    .filter((r) => !r.draft)
    .map((r, i) => {
      const tag = escapeHtml(r.tag_name || r.name || "release");
      const name =
        r.name && r.name !== r.tag_name ? escapeHtml(r.name) : "";
      const date = formatReleaseDate(r.published_at || r.created_at);
      const body = simpleMarkdown(r.body || "");
      const url = escapeHtml(r.html_url || GH_RELEASES_PAGE);
      const latest =
        i === 0
          ? '<span class="cl-badge">latest</span>'
          : "";
      const pre = r.prerelease
        ? '<span class="cl-badge" style="border-color:#666;color:#aaa;background:#2a2a2a">pre</span>'
        : "";

      return `
        <article class="cl-card">
          <div class="cl-card-head">
            <span class="cl-tag">${tag}</span>
            ${name ? `<span class="cl-name">${name}</span>` : ""}
            ${latest}${pre}
            <span class="cl-date">${date}</span>
          </div>
          <div class="cl-body">${body}</div>
          <a class="cl-link" href="${url}" target="_blank" rel="noopener noreferrer">Открыть на GitHub →</a>
        </article>`;
    })
    .join("");
}

function readChangelogCache() {
  try {
    const raw = localStorage.getItem(LS_CHANGELOG);
    if (!raw) return null;
    const data = JSON.parse(raw);
    if (!data || !Array.isArray(data.releases) || !data.ts) return null;
    if (Date.now() - data.ts > CHANGELOG_TTL_MS) return null;
    return data.releases;
  } catch {
    return null;
  }
}

function writeChangelogCache(releases) {
  try {
    localStorage.setItem(
      LS_CHANGELOG,
      JSON.stringify({ ts: Date.now(), releases })
    );
  } catch {
    /* ignore */
  }
}

async function fetchReleasesFromGitHub() {
  const res = await fetch(GH_RELEASES_URL, {
    headers: {
      Accept: "application/vnd.github+json",
      // GitHub recommends a UA; browser sends one automatically
    },
    cache: "no-cache",
  });

  if (res.status === 403 || res.status === 429) {
    const err = new Error("rate_limit");
    err.status = res.status;
    throw err;
  }
  if (!res.ok) {
    const err = new Error("http_" + res.status);
    err.status = res.status;
    throw err;
  }
  return res.json();
}

async function loadChangelog() {
  if (changelogLoading) return;
  const status = document.getElementById("changelog-status");

  // Кэш
  const cached = readChangelogCache();
  if (cached) {
    renderReleases(cached);
    changelogLoaded = true;
    // тихий refresh в фоне
    refreshChangelogInBackground();
    return;
  }

  if (changelogLoaded) return;
  changelogLoading = true;

  if (status) {
    status.className = "changelog-status";
    status.textContent = "Загрузка релизов с GitHub…";
  }

  try {
    const releases = await fetchReleasesFromGitHub();
    writeChangelogCache(releases);
    renderReleases(releases);
    changelogLoaded = true;
  } catch (e) {
    if (status) {
      status.className = "changelog-status is-error";
      if (e && e.message === "rate_limit") {
        status.innerHTML =
          "GitHub временно ограничил запросы (лимит API). Попробуй позже или открой " +
          `<a class="inline-link" href="${GH_RELEASES_PAGE}" target="_blank" rel="noopener noreferrer">релизы на GitHub</a>.`;
      } else {
        status.innerHTML =
          "Не удалось загрузить changelog. " +
          `<a class="inline-link" href="${GH_RELEASES_PAGE}" target="_blank" rel="noopener noreferrer">Смотреть на GitHub →</a>`;
      }
    }
  } finally {
    changelogLoading = false;
  }
}

async function refreshChangelogInBackground() {
  try {
    const releases = await fetchReleasesFromGitHub();
    writeChangelogCache(releases);
    renderReleases(releases);
  } catch {
    /* keep cache */
  }
}

/** Анимация появления блоков при скролле вниз */
function wireScrollReveal() {
  const nodes = document.querySelectorAll(".reveal");
  if (!nodes.length) return;

  if (reduceMotion || typeof IntersectionObserver === "undefined") {
    nodes.forEach((el) => el.classList.add("is-visible"));
    return;
  }

  const io = new IntersectionObserver(
    (entries) => {
      entries.forEach((entry) => {
        if (!entry.isIntersecting) return;
        entry.target.classList.add("is-visible");
        io.unobserve(entry.target);
      });
    },
    {
      root: null,
      rootMargin: "0px 0px -8% 0px",
      threshold: 0.12,
    }
  );

  nodes.forEach((el) => io.observe(el));
}

async function boot() {
  wireDownload();
  wireTabs();
  startEntrance();
  wireScrollReveal();

  const cachedV = readLocal(LS_VISITS, 0);
  const cachedD = readLocal(LS_DOWNLOADS, 0);
  if (cachedV > 0 || cachedD > 0) {
    setCountUI(cachedV || "—", cachedD || "—");
  }

  const [visitors, downloads] = await Promise.all([
    initVisitors(),
    initDownloadsDisplay(),
  ]);
  setCountUI(visitors, downloads, { animate: true });
}

if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", boot);
} else {
  boot();
}
