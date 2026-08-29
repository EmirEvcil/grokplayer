import { spawn } from "node:child_process";
import { createConnection } from "node:net";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { writeFileSync } from "node:fs";

const here = dirname(fileURLToPath(import.meta.url));
const ext = join(here, "..", "..", "extension");
const chrome = "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";
const profile = join(here, "chrome-profile-hdfilm-" + Date.now());
const pageUrl = process.argv[2] || "https://www.hdfilmcehennemi.nl/the-last-scene-2026/";
const port = 9335;

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

const child = spawn(chrome, [
  `--user-data-dir=${profile}`,
  `--remote-debugging-port=${port}`,
  "--disable-first-run-ui",
  "--no-first-run",
  "--no-default-browser-check",
  `--disable-extensions-except=${ext}`,
  `--load-extension=${ext}`,
  "--autoplay-policy=no-user-gesture-required",
  "about:blank"
], { detached: true, stdio: "ignore" });
child.unref();

await waitPort(port, 25000);
const targets = await fetch(`http://127.0.0.1:${port}/json/list`).then((res) => res.json());
const page = targets.find((item) => item.type === "page");
if (!page) {
  throw new Error("No Chrome page");
}

const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((resolve, reject) => {
  ws.addEventListener("open", resolve);
  ws.addEventListener("error", reject);
});

let nextId = 1;
const pending = new Map();
ws.addEventListener("message", (event) => {
  const msg = JSON.parse(event.data);
  if (msg.id && pending.has(msg.id)) {
    pending.get(msg.id)(msg);
    pending.delete(msg.id);
  }
});

function send(method, params = {}) {
  const id = nextId++;
  ws.send(JSON.stringify({ id, method, params }));
  return new Promise((resolve) => pending.set(id, resolve));
}

await send("Page.enable");
await send("Runtime.enable");
await send("Network.enable");
await send("Page.navigate", { url: pageUrl });
await new Promise((r) => setTimeout(r, 8000));

const dumpExpr = `(() => {
  const videos = [...document.querySelectorAll("video")].map(v => ({
    src: v.currentSrc || v.src || "",
    paused: v.paused,
    time: v.currentTime,
    dur: v.duration,
    w: v.clientWidth,
    h: v.clientHeight
  }));
  const iframes = [...document.querySelectorAll("iframe")].map(f => ({
    src: f.src || f.getAttribute("data-src") || "",
    w: f.getBoundingClientRect().width,
    h: f.getBoundingClientRect().height,
    cls: String(f.className || "")
  }));
  const chip = document.querySelector("#grokplayer-chip, .grokplayer-chip");
  const api = window.GrokPlayerSniff;
  const sniff = api ? api() : null;
  const surfaces = api && api.primarySurfaces ? api.primarySurfaces().length : -1;
  return { href: location.href, title: document.title, chip: !!chip, surfaces, sniff, videos, iframes };
})()`;

function val(msg) {
  return msg && msg.result && msg.result.value !== undefined
    ? msg.result.value
    : msg && msg.result && msg.result.result && msg.result.result.value;
}

const before = await send("Runtime.evaluate", { expression: dumpExpr, returnByValue: true });
console.log("BEFORE_PLAY", JSON.stringify(val(before), null, 2));

await send("Runtime.evaluate", {
  expression: `(() => {
    const play = document.querySelector(".play-that-video, .po-btn, [aria-label='Play video']");
    if (play) { play.click(); return "clicked " + play.className; }
    const video = document.querySelector("video");
    if (video) { video.play(); return "video.play"; }
    return "no-play-control";
  })()`,
  returnByValue: true
});

await new Promise((r) => setTimeout(r, 6000));

const frames = await fetch(`http://127.0.0.1:${port}/json/list`).then((res) => res.json());
const dumps = [];
for (const target of frames.filter((item) => item.type === "page" || item.type === "iframe")) {
  if (!target.webSocketDebuggerUrl) {
    continue;
  }
  const inner = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    inner.addEventListener("open", resolve);
    inner.addEventListener("error", reject);
  });
  let iid = 1;
  const ipending = new Map();
  inner.addEventListener("message", (event) => {
    const msg = JSON.parse(event.data);
    if (msg.id && ipending.has(msg.id)) {
      ipending.get(msg.id)(msg);
      ipending.delete(msg.id);
    }
  });
  const isend = (method, params = {}) => {
    const id = iid++;
    inner.send(JSON.stringify({ id, method, params }));
    return new Promise((resolve) => ipending.set(id, resolve));
  };
  await isend("Runtime.enable");
  await isend("Runtime.evaluate", {
    expression: `(() => {
      try { if (window.jwplayer) { const p = jwplayer(); if (p && p.play) p.play(); } } catch (e) {}
      document.querySelectorAll("video").forEach((v) => { try { v.muted = false; v.play(); } catch (e) {} });
      return true;
    })()`,
    returnByValue: true
  });
  await new Promise((r) => setTimeout(r, 8000));
  const media = await isend("Runtime.evaluate", {
    expression: `(() => {
      const videos = [...document.querySelectorAll("video")].map(v => ({
        src: (v.currentSrc || v.src || "").slice(0, 240),
        paused: v.paused,
        time: v.currentTime,
        dur: v.duration,
        w: v.clientWidth,
        h: v.clientHeight
      }));
      const perf = performance.getEntriesByType("resource").map(e => e.name).filter(u =>
        /m3u8|mp4|mpd|hls|playmix|cdnimages|jwplayer|master\\.txt|tiktokcdn|scontent|fbcdn/i.test(u)
      ).slice(0, 40);
      const api = window.GrokPlayerSniff;
      return {
        href: location.href,
        chip: !!document.querySelector("#grokplayer-chip, .grokplayer-chip"),
        sniff: api ? api() : null,
        videos,
        perf
      };
    })()`,
    returnByValue: true
  });
  dumps.push({ url: target.url, value: val(media) });
  try { inner.close(); } catch {}
}

const after = await send("Runtime.evaluate", { expression: dumpExpr, returnByValue: true });
const out = {
  before: val(before),
  after: val(after),
  frames: dumps
};
writeFileSync(join(here, "hdfilm-live.json"), JSON.stringify(out, null, 2));
console.log("AFTER_PLAY", JSON.stringify(val(after), null, 2));
console.log("FRAMES", dumps.length);
dumps.forEach((item, i) => {
  console.log("FRAME", i, item.url);
  console.log(JSON.stringify(item.value, null, 2));
});
try { ws.close(); } catch {}
setTimeout(() => process.exit(0), 400);
