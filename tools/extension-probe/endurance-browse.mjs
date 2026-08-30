import { spawn } from "node:child_process";
import { mkdirSync, readFileSync, writeFileSync, existsSync } from "node:fs";
import { createConnection } from "node:net";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..", "..");
const ext = join(root, "extension");
const chrome = "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";
const port = Number(process.env.GROK_CDP_PORT || 9340);
const profile = join(here, "chrome-profile-endurance-" + Date.now());
const listPath = process.argv[2] || join(here, "endurance-browse-urls.txt");
const outPath = process.argv[3] || join(here, "endurance-browse.json");

function readUrls(path) {
  if (!existsSync(path)) {
    throw new Error("url list missing: " + path);
  }
  return readFileSync(path, "utf8")
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line.length > 0 && !line.startsWith("#") && !line.startsWith("//"));
}

function waitPort(openPort, ms) {
  const start = Date.now();
  return new Promise((resolve, reject) => {
    const tick = () => {
      const socket = createConnection({ port: openPort, host: "127.0.0.1" }, () => {
        socket.end();
        resolve();
      });
      socket.on("error", () => {
        if (Date.now() - start > ms) {
          reject(new Error("Chrome debug port did not open"));
          return;
        }
        setTimeout(tick, 250);
      });
    };
    tick();
  });
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function looksMedia(url) {
  if (!url || !/^https?:\/\//i.test(url)) {
    return false;
  }
  if (/\/image\d+\.(jpg|jpeg|png|webp)|\/txt\/master\.txt|\/rekla\/|doubleclick|googlesyndication|imasdk|\/ads?\//i.test(url)) {
    return false;
  }
  if (/\.(jpg|jpeg|png|webp|gif)(?:$|\?)/i.test(url) && !/\.(mp4|m3u8|webm|mov)(?:$|\?)/i.test(url)) {
    return false;
  }
  return /(?:\.m3u8|\.m3u|\.mpd|\.mp4|\.mkv|\.webm|\.mov|master\.txt|playlist\.txt)(?:$|\?|\/)/i.test(url) ||
    /googlevideo|live-video\.net|stream\.kick\.com|ttvnw\.net|rumble\.cloud|hls-vod|tiktokcdn|byteoversea|scontent|cdninstagram|fbcdn\.net|playmix|rapidrame|dmcdn\.net|\/hls\//i.test(url);
}

function usesPageCatalog(url) {
  if (/(?:youtube\.com|youtu\.be)/i.test(url || "")) return true;
  if (/(?:dailymotion\.com|dai\.ly)/i.test(url || "")) return true;
  if (/twitch\.tv/i.test(url || "")) return true;
  if (/kick\.com/i.test(url || "")) {
    try {
      const parts = new URL(url).pathname.replace(/^\/+|\/+$/g, "").split("/");
      return !(parts[0] === "video" || parts[1] === "videos" || parts[1] === "clips");
    } catch {
      return true;
    }
  }
  return false;
}

function catalogUrl(url) {
  if (!url) return "";
  if (/(?:youtube\.com|youtu\.be)/i.test(url)) return url;
  return String(url).split("#")[0].split("?")[0];
}

function transferOf(pageUrl, mediaUrl) {
  if (usesPageCatalog(pageUrl)) {
    return catalogUrl(pageUrl);
  }
  return looksMedia(mediaUrl) ? mediaUrl : "";
}

mkdirSync(profile, { recursive: true });
const urls = readUrls(listPath);
const child = spawn(chrome, [
  `--user-data-dir=${profile}`,
  `--remote-debugging-port=${port}`,
  "--disable-first-run-ui",
  "--no-first-run",
  "--no-default-browser-check",
  "--disable-sync",
  "--mute-audio",
  "--autoplay-policy=no-user-gesture-required",
  "--disable-features=DisableLoadExtensionCommandLineSwitch",
  "--enable-unsafe-extension-debugging",
  `--disable-extensions-except=${ext}`,
  `--load-extension=${ext}`,
  "about:blank"
], { detached: true, stdio: "ignore" });
child.unref();

await waitPort(port, 30000);

async function listTargets() {
  return fetch(`http://127.0.0.1:${port}/json/list`).then((res) => res.json());
}

function attach(target) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(target.webSocketDebuggerUrl);
    const pending = new Map();
    const network = [];
    let nextId = 1;
    ws.addEventListener("open", () => resolve({
      ws,
      network,
      send(method, params = {}) {
        const id = nextId++;
        ws.send(JSON.stringify({ id, method, params }));
        return new Promise((done) => pending.set(id, done));
      }
    }));
    ws.addEventListener("error", reject);
    ws.addEventListener("message", (event) => {
      const msg = JSON.parse(event.data);
      if (msg.id && pending.has(msg.id)) {
        pending.get(msg.id)(msg);
        pending.delete(msg.id);
      }
      if (msg.method === "Network.requestWillBeSent") {
        const href = msg.params?.request?.url || "";
        if (looksMedia(href) && !network.includes(href)) {
          network.push(href);
        }
      }
    });
  });
}

function val(msg) {
  if (msg && msg.result && msg.result.exceptionDetails) {
    return { error: msg.result.exceptionDetails.text || "evaluate failed" };
  }
  return msg && msg.result && msg.result.result
    ? msg.result.result.value
    : msg?.result?.value;
}

const dumpExpr = `(() => {
  const videos = [...document.querySelectorAll("video")].map((v) => ({
    src: (v.currentSrc || v.src || "").slice(0, 400),
    paused: v.paused,
    time: Number.isFinite(v.currentTime) ? v.currentTime : 0,
    dur: Number.isFinite(v.duration) ? v.duration : 0,
    w: v.clientWidth,
    h: v.clientHeight
  }));
  const iframes = [...document.querySelectorAll("iframe")].map((f) => ({
    src: (f.src || f.getAttribute("data-src") || "").slice(0, 400),
    w: Math.round(f.getBoundingClientRect().width),
    h: Math.round(f.getBoundingClientRect().height),
    cls: String(f.className || "").slice(0, 80)
  }));
  const chip = document.querySelector("#grokplayer-chip, .grokplayer-chip");
  const api = window.GrokPlayerSniff;
  let sniff = null;
  try { sniff = api ? api() : null; } catch (e) { sniff = { error: String(e) }; }
  const perf = performance.getEntriesByType("resource").map((e) => e.name).filter((u) =>
    /m3u8|mp4|mpd|hls|playmix|master\\.txt|tiktokcdn|scontent|fbcdn|googlevideo|ttvnw|kick\\.com|rumble|dmcdn|rapidrame|playturka/i.test(u)
  ).slice(0, 50);
  return {
    href: location.href,
    title: document.title.slice(0, 180),
    chip: !!(chip && chip.getBoundingClientRect().width > 0),
    sniff,
    videos,
    iframes,
    perf
  };
})()`;

const playExpr = `(() => {
  const clicked = [];
  const sels = [
    "button", "[role='button']", ".play-that-video", ".po-btn", ".vjs-big-play-button",
    ".ytp-large-play-button", ".jw-icon-display", "[aria-label='Play']",
    "[aria-label='Play video']", ".play", "#play"
  ];
  const nodes = [];
  sels.forEach((sel) => nodes.push(...document.querySelectorAll(sel)));
  for (const node of nodes) {
    const text = ((node.innerText || node.getAttribute("aria-label") || node.className || "") + "").toLowerCase();
    if (/play|izle|oynat|▶|watch/.test(text) && node.offsetParent !== null) {
      try { node.click(); clicked.push((node.className || node.tagName + "").slice(0, 60)); } catch (e) {}
    }
  }
  document.querySelectorAll("video").forEach((v) => {
    try { v.muted = true; v.play(); } catch (e) {}
  });
  try { if (window.jwplayer) { const p = jwplayer(); if (p && p.play) p.play(); } } catch (e) {}
  return clicked.slice(0, 8);
})()`;

async function dumpAllFrames() {
  const targets = await listTargets();
  const frames = [];
  const media = [];
  for (const target of targets.filter((item) => item.type === "page" || item.type === "iframe")) {
    if (!target.webSocketDebuggerUrl) {
      continue;
    }
    let session;
    try {
      session = await attach(target);
    } catch {
      continue;
    }
    try {
      await session.send("Runtime.enable");
      await session.send("Runtime.evaluate", { expression: playExpr, returnByValue: true });
      const dump = val(await session.send("Runtime.evaluate", { expression: dumpExpr, returnByValue: true }));
      frames.push({ url: target.url, type: target.type, dump, network: session.network.slice() });
      const push = (href) => {
        if (looksMedia(href) && !media.includes(href)) {
          media.push(href);
        }
      };
      (dump && dump.videos || []).forEach((v) => push(v.src));
      (dump && dump.perf || []).forEach(push);
      session.network.forEach(push);
      if (dump && dump.sniff) {
        const info = dump.sniff.info || dump.sniff.latest || dump.sniff;
        if (info && info.url) push(info.url);
        if (dump.sniff.url) push(dump.sniff.url);
      }
    } catch {
    } finally {
      try { session.ws.close(); } catch {}
    }
  }
  return { frames, media };
}

async function probe(url) {
  const started = Date.now();
  const targets = await listTargets();
  const page = targets.find((item) => item.type === "page") || targets[0];
  if (!page) {
    return { url, status: "fail", detail: "no page target", ms: Date.now() - started };
  }
  const session = await attach(page);
  await session.send("Network.enable");
  await session.send("Page.enable");
  await session.send("Runtime.enable");
  if (/youtube/.test(url)) {
    await session.send("Network.setCookie", { name: "SOCS", value: "CAI", domain: ".youtube.com", path: "/" });
    await session.send("Network.setCookie", { name: "CONSENT", value: "YES+", domain: ".youtube.com", path: "/" });
  }
  await session.send("Page.navigate", { url });
  await sleep(/instagram|tiktok|hdfilm|filmizle|fullhd|jetfilm/i.test(url) ? 9000 : 7000);
  await session.send("Runtime.evaluate", { expression: playExpr, returnByValue: true });
  await sleep(6000);
  const after = await dumpAllFrames();
  const main = val(await session.send("Runtime.evaluate", { expression: dumpExpr, returnByValue: true }));
  const network = session.network.slice();
  try { session.ws.close(); } catch {}
  const media = [];
  const add = (href) => {
    if (looksMedia(href) && !media.includes(href)) media.push(href);
  };
  network.forEach(add);
  after.media.forEach(add);
  (main && main.videos || []).forEach((v) => add(v.src));
  (main && main.perf || []).forEach(add);
  const sniffUrl = main && main.sniff && (main.sniff.url || (main.sniff.info && main.sniff.info.url));
  if (sniffUrl) add(sniffUrl);
  const playing = (main && main.videos || []).find((v) => v.src && !v.paused && v.w > 40) ||
    (main && main.videos || []).find((v) => v.src && v.w > 40);
  const mediaUrl = (playing && playing.src) || media[0] || "";
  const transfer = transferOf(url, mediaUrl);
  const chip = !!(main && main.chip) || after.frames.some((f) => f.dump && f.dump.chip);
  const ok = !!(chip || transfer);
  return {
    url,
    href: main && main.href,
    title: main && main.title,
    chip,
    transfer,
    mediaUrl,
    media: media.slice(0, 12),
    videos: main && main.videos,
    iframes: main && main.iframes,
    sniff: main && main.sniff,
    frames: after.frames.map((f) => ({
      url: f.url,
      type: f.type,
      chip: !!(f.dump && f.dump.chip),
      videos: f.dump && f.dump.videos,
      sniff: f.dump && f.dump.sniff,
      iframeCount: f.dump && f.dump.iframes ? f.dump.iframes.length : 0
    })),
    status: ok ? "ok" : "fail",
    detail: (chip ? "chip " : "no-chip ") + (transfer ? "transfer" : "no-transfer") +
      " videos=" + ((main && main.videos && main.videos.length) || 0) +
      " media=" + media.length +
      " ms=" + (Date.now() - started)
  };
}

const rows = [];
let failed = 0;
for (const url of urls) {
  let row;
  try {
    row = await probe(url);
  } catch (err) {
    row = { url, status: "fail", detail: String(err && err.message || err) };
  }
  rows.push(row);
  if (row.status !== "ok") failed++;
  console.log((row.status === "ok" ? "OK  " : "FAIL") + "  " + url + "  " + (row.detail || ""));
}

const report = {
  started: new Date().toISOString(),
  passed: urls.length - failed,
  failed,
  total: urls.length,
  rows
};
writeFileSync(outPath, JSON.stringify(report, null, 2));
console.log("wrote " + outPath);
console.log("summary passed=" + (urls.length - failed) + " failed=" + failed + " total=" + urls.length);
try { process.kill(child.pid); } catch {}
setTimeout(() => process.exit(failed === 0 ? 0 : 1), 400);
