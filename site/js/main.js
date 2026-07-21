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

async function boot() {
  wireDownload();
  startEntrance();

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
