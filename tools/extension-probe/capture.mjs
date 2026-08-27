import { spawn } from "node:child_process";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { createConnection } from "node:net";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..", "..");
const ext = join(root, "extension");
const profile = join(here, "chrome-profile-auto-" + Date.now());
const chrome = "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";
const video = process.argv[2] || "https://www.youtube.com/watch?v=Qtl8lJwbd4g";
const port = 9333;
const probeSrc = readFileSync(join(ext, "content", "probe-page.js"), "utf8");

mkdirSync(profile, { recursive: true });

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
  "--disable-sync",
  "about:blank"
], { detached: true, stdio: "ignore" });
child.unref();

await waitPort(port, 20000);
const targets = await fetch(`http://127.0.0.1:${port}/json/list`).then((res) => res.json());
const page = targets.find((item) => item.type === "page" && /youtube/.test(item.url || "")) || targets.find((item) => item.type === "page");
if (!page) {
  throw new Error("No Chrome page target");
}

const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((resolve, reject) => {
  ws.addEventListener("open", resolve);
  ws.addEventListener("error", reject);
});

let nextId = 1;
const pending = new Map();
const logs = [];
ws.addEventListener("message", (event) => {
  const msg = JSON.parse(event.data);
  if (msg.id && pending.has(msg.id)) {
    pending.get(msg.id)(msg);
    pending.delete(msg.id);
  }
  if (msg.method === "Runtime.consoleAPICalled") {
    const raw = JSON.stringify(msg.params.args || []);
    const text = (msg.params.args || []).map((arg) => arg.value ?? arg.preview?.description ?? arg.description ?? "").join(" ");
    if (/GrokPlayer|movie_player|getAudioTrack|caption/i.test(text + raw)) {
      logs.push({ type: msg.params.type, text: text.slice(0, 4000), args: msg.params.args });
      console.log("[console]", msg.params.type, text.slice(0, 500));
    }
  }
});

function send(method, params = {}) {
  const id = nextId++;
  ws.send(JSON.stringify({ id, method, params }));
  return new Promise((resolve) => pending.set(id, resolve));
}

await send("Runtime.enable");
await send("Network.enable");
await send("Page.enable");
await send("Network.setCookie", {
  name: "SOCS",
  value: "CAI",
  domain: ".youtube.com",
  path: "/"
});
await send("Network.setCookie", {
  name: "CONSENT",
  value: "YES+",
  domain: ".youtube.com",
  path: "/"
});
await send("Page.addScriptToEvaluateOnNewDocument", { source: probeSrc });
await send("Page.navigate", { url: video });
await new Promise((resolve) => setTimeout(resolve, 18000));
await send("Runtime.evaluate", { expression: probeSrc });
await new Promise((resolve) => setTimeout(resolve, 1000));

const isolated = await send("Runtime.evaluate", {
  expression: `JSON.stringify({
    href: location.href,
    title: document.title,
    chip: !!document.getElementById('grokplayer-chip'),
    probeAttr: document.documentElement.getAttribute('data-grokplayer-probe'),
    dumpAttr: document.documentElement.getAttribute('data-grokplayer-dump'),
    player: !!document.getElementById('movie_player'),
    pageDump: window.__grokPlayerPageDump || null
  })`,
  returnByValue: true
});
const targetsAfter = await fetch(`http://127.0.0.1:${port}/json/list`).then((res) => res.json());
console.log("targets", targetsAfter.map((item) => item.type + " " + (item.url || item.title)));

let parsed = isolated.result && isolated.result.value;
try {
  parsed = JSON.parse(parsed);
} catch {
}
const out = join(here, "last-dump.json");
writeFileSync(out, JSON.stringify({ page: parsed, logs, isolated }, null, 2));
console.log("wrote", out);
try { ws.close(); } catch {}
setTimeout(() => process.exit(0), 200);
