import { spawn } from "node:child_process";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { createConnection } from "node:net";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..", "..");
const ext = join(root, "extension");
const profile = join(here, "chrome-profile-live-" + Date.now());
const chrome = "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";
const video = process.argv[2] || "https://www.youtube.com/watch?v=Qtl8lJwbd4g&t=159s";
const port = Number(process.env.GROK_CDP_PORT || 9335);
const langsSrc = readFileSync(join(ext, "content", "langs.js"), "utf8");

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

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

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

await waitPort(port, 25000);

async function listTargets() {
  return fetch(`http://127.0.0.1:${port}/json/list`).then((res) => res.json());
}

let targets = await listTargets();
let page = targets.find((item) => item.type === "page") || targets[0];
if (!page) {
  throw new Error("No Chrome page target");
}

async function attach(target) {
  const ws = new WebSocket(target.webSocketDebuggerUrl);
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
      const text = (msg.params.args || []).map((arg) => arg.value ?? arg.preview?.description ?? arg.description ?? "").join(" ");
      if (/GrokPlayer|resolve|getAudioTrack|caption/i.test(text)) {
        logs.push(text.slice(0, 2000));
      }
    }
  });
  function send(method, params = {}) {
    const id = nextId++;
    ws.send(JSON.stringify({ id, method, params }));
    return new Promise((resolve) => pending.set(id, resolve));
  }
  return { ws, send, logs };
}

let session = await attach(page);
await session.send("Runtime.enable");
await session.send("Network.enable");
await session.send("Page.enable");
await session.send("Network.setCookie", { name: "SOCS", value: "CAI", domain: ".youtube.com", path: "/" });
await session.send("Network.setCookie", { name: "CONSENT", value: "YES+", domain: ".youtube.com", path: "/" });
await session.send("Page.addScriptToEvaluateOnNewDocument", { source: langsSrc });
await session.send("Page.navigate", { url: video });
await sleep(12000);

targets = await listTargets();
page = targets.find((item) => item.type === "page" && /youtube\.com\/watch/.test(item.url || "")) ||
  targets.find((item) => item.type === "page" && /youtube/.test(item.url || "")) ||
  page;
try { session.ws.close(); } catch {}
session = await attach(page);
await session.send("Runtime.enable");
await session.send("Page.enable");

async function evaluate(expression, awaitPromise = false) {
  const result = await session.send("Runtime.evaluate", {
    expression,
    returnByValue: true,
    awaitPromise
  });
  if (result.result && result.result.exceptionDetails) {
    return { error: result.result.exceptionDetails.text || "evaluate failed" };
  }
  return result.result && result.result.result ? result.result.result.value : result.result?.value;
}

for (let i = 0; i < 20; i++) {
  const ready = await evaluate(`!!(document.getElementById("movie_player") && document.querySelector("video"))`);
  if (ready) {
    break;
  }
  await sleep(1000);
}

await evaluate(`(function(){const v=document.querySelector("video"); if(v){v.muted=true; v.play().catch(()=>{});} const p=document.getElementById("movie_player"); if(p&&p.playVideo){try{p.playVideo();}catch(e){}}})()`);
await sleep(4000);
await evaluate(langsSrc);

const research = await evaluate(`(function(){
  if (!window.GrokPlayerLangs) { return { error: "langs missing" }; }
  const player = document.getElementById("movie_player") || document.querySelector(".html5-video-player");
  const video = document.querySelector("video");
  const button = document.querySelector(".ytp-subtitles-button");
  function safe(fn){ try { return fn(); } catch (e) { return { error: String(e && e.message || e) }; } }
  const pr = safe(() => player && player.getPlayerResponse ? player.getPlayerResponse() : (window.ytInitialPlayerResponse || null));
  const renderer = pr && !pr.error && pr.captions && pr.captions.playerCaptionsTracklistRenderer ? pr.captions.playerCaptionsTracklistRenderer : null;
  const formats = [].concat((pr && pr.streamingData && pr.streamingData.adaptiveFormats) || []);
  const adaptiveAudio = [];
  const seen = new Set();
  formats.forEach((item) => {
    const track = item && item.audioTrack;
    if (!track || !track.id || seen.has(track.id)) return;
    seen.add(track.id);
    adaptiveAudio.push({ id: track.id, displayName: track.displayName, audioIsDefault: track.audioIsDefault });
  });
  const audio = safe(() => player && player.getAudioTrack && player.getAudioTrack());
  const available = safe(() => player && player.getAvailableAudioTracks && player.getAvailableAudioTracks());
  const captionTrack = safe(() => player && player.getOption && player.getOption("captions", "track"));
  const tracklist = safe(() => player && player.getOption && player.getOption("captions", "tracklist"));
  const translations = safe(() => player && player.getOption && player.getOption("captions", "translationLanguages"));
  function slimAudio(track){
    if (!track || track.error) return track;
    const keys = [];
    try { keys.push.apply(keys, Object.keys(track)); } catch (e) {}
    return {
      id: track.id,
      keys: keys,
      j7id: track.j7 && track.j7.id,
      j7name: track.j7 && track.j7.name,
      name: track.name || track.displayName || (track.j7 && track.j7.name),
      isSelected: !!(track.isSelected || track.selected),
      isDefault: track.j7 && track.j7.isDefault,
      xtags: track.xtags,
      code: window.GrokPlayerLangs.audioCode(track)
    };
  }
  function slimCap(track){
    if (!track || track.error) return track;
    return {
      languageCode: track.languageCode,
      languageName: track.languageName || track.displayName,
      kind: track.kind,
      vssId: track.vssId,
      isDefault: track.isDefault,
      translationLanguage: track.translationLanguage,
      code: window.GrokPlayerLangs.captionCode(track)
    };
  }
  const snap = {
    getAudioTrack: audio && !audio.error ? audio : null,
    getAvailableAudioTracks: Array.isArray(available) ? available : [],
    captionTrack: captionTrack && !captionTrack.error ? captionTrack : null,
    captionTracklist: Array.isArray(tracklist) ? tracklist : [],
    translationLanguages: Array.isArray(translations) ? translations : [],
    captionsOn: !!(button && button.getAttribute("aria-pressed") === "true"),
    textTracks: video ? [...video.textTracks].map((item) => ({ language: item.language, label: item.label, mode: item.mode })) : [],
    playerAudioTracks: adaptiveAudio,
    playerCaptionTracks: renderer && renderer.captionTracks || [],
    playerTranslationLanguages: renderer && renderer.translationLanguages || []
  };
  const resolvedAuto = window.GrokPlayerLangs.resolve(snap, { audioPref: "auto", subPref: "auto" });
  return {
    href: location.href,
    title: document.title,
    chip: !!document.getElementById("grokplayer-chip"),
    player: !!player,
    button: button ? { pressed: button.getAttribute("aria-pressed"), label: button.getAttribute("aria-label") } : null,
    getOptions: safe(() => player && player.getOptions && player.getOptions()),
    captionOptions: safe(() => player && player.getOptions && player.getOptions("captions")),
    audioNow: slimAudio(audio),
    availableAudio: Array.isArray(available) ? available.map(slimAudio) : available,
    captionNow: slimCap(captionTrack),
    tracklist: Array.isArray(tracklist) ? tracklist.map(slimCap) : tracklist,
    translationCount: Array.isArray(translations) ? translations.length : (translations && translations.error) || 0,
    adaptiveAudio,
    playerCaptionCodes: (renderer && renderer.captionTracks || []).map((item) => item.languageCode + (item.kind ? ":" + item.kind : "")),
    playerTranslationCodes: (renderer && renderer.translationLanguages || []).map((item) => item.languageCode),
    resolvedAuto
  };
})()`);

async function applyAndResolve(audioCode, captionCode, prefs) {
  return evaluate(`(function(){
    const player = document.getElementById("movie_player");
    const button = document.querySelector(".ytp-subtitles-button");
    const wantAudio = ${JSON.stringify(audioCode)};
    const wantCaption = ${JSON.stringify(captionCode)};
    let audioSet = false;
    let captionSet = false;
    try {
      const tracks = player && player.getAvailableAudioTracks ? player.getAvailableAudioTracks() : [];
      const hit = (tracks || []).find((item) => {
        const code = window.GrokPlayerLangs.audioCode(item);
        return code === wantAudio || (item.j7 && item.j7.id === wantAudio);
      });
      if (hit && player.setAudioTrack) {
        player.setAudioTrack(hit);
        audioSet = true;
      }
    } catch (e) {}
    try {
      if (wantCaption === "off") {
        if (button && button.getAttribute("aria-pressed") === "true") button.click();
        captionSet = true;
      } else if (player && player.setOption) {
        if (button && button.getAttribute("aria-pressed") !== "true") button.click();
        player.setOption("captions", "track", { languageCode: wantCaption.replace(/:asr$/, "") });
        captionSet = true;
      }
    } catch (e) {}
    const video = document.querySelector("video");
    const audio = player && player.getAudioTrack && player.getAudioTrack();
    const captionTrack = player && player.getOption && player.getOption("captions", "track");
    const pr = player && player.getPlayerResponse && player.getPlayerResponse();
    const renderer = pr && pr.captions && pr.captions.playerCaptionsTracklistRenderer;
    const snap = {
      getAudioTrack: audio,
      getAvailableAudioTracks: player && player.getAvailableAudioTracks ? player.getAvailableAudioTracks() : [],
      captionTrack: captionTrack,
      captionTracklist: player && player.getOption ? player.getOption("captions", "tracklist") || [] : [],
      translationLanguages: player && player.getOption ? player.getOption("captions", "translationLanguages") || [] : [],
      captionsOn: !!(document.querySelector(".ytp-subtitles-button[aria-pressed='true']")),
      textTracks: video ? [...video.textTracks].map((item) => ({ language: item.language, label: item.label, mode: item.mode })) : [],
      playerAudioTracks: [],
      playerCaptionTracks: renderer && renderer.captionTracks || [],
      playerTranslationLanguages: renderer && renderer.translationLanguages || []
    };
    const resolved = window.GrokPlayerLangs.resolve(snap, ${JSON.stringify(prefs)});
    return {
      wantAudio, wantCaption, audioSet, captionSet,
      liveAudio: window.GrokPlayerLangs.audioCode(audio),
      liveCaption: window.GrokPlayerLangs.captionCode(captionTrack),
      captionsOn: snap.captionsOn,
      resolved
    };
  })()`);
}

const cases = [];
if (research && !research.error) {
  const audioCodes = (research.availableAudio || []).map((item) => item && item.code).filter(Boolean);
  const captionCodes = (research.resolvedAuto && research.resolvedAuto.available.captions || [])
    .map((item) => item.code)
    .filter(Boolean);
  const picks = [];
  for (const code of ["bn", "ru", "zh-Hans", "zh-Hant", "tr", "ar", "original", "hi", "pt-BR"]) {
    if (audioCodes.includes(code) || code === "original") {
      picks.push(["audio-auto-" + code, code, research.button && research.button.pressed === "true" ? "tr" : "off", { audioPref: "auto", subPref: "auto" }]);
    }
  }
  picks.push(["pref-ru-zh", "tr", "tr", { audioPref: "ru", subPref: "zh-Hans" }]);
  picks.push(["pref-bn-off", "tr", "tr", { audioPref: "bn", subPref: "off" }]);
  picks.push(["pref-original-ja", "tr", "tr", { audioPref: "original", subPref: "ja" }]);
  picks.push(["pref-hi-ko", "bn", "ru", { audioPref: "hi", subPref: "ko" }]);
  picks.push(["auto-caps-off", "ru", "off", { audioPref: "auto", subPref: "auto" }]);
  for (const item of picks) {
    const result = await applyAndResolve(item[1], item[2], item[3]);
    await sleep(400);
    cases.push({ name: item[0], request: { audio: item[1], sub: item[2], pref: item[3] }, result });
  }
  cases.push({
    name: "catalog-coverage",
    audioCodes,
    captionSample: captionCodes.slice(0, 30),
    captionCount: captionCodes.length,
    hasBn: audioCodes.includes("bn") || captionCodes.includes("bn"),
    hasRu: audioCodes.includes("ru") || captionCodes.includes("ru"),
    hasZhHans: audioCodes.includes("zh-Hans") || captionCodes.includes("zh-Hans"),
    hasZhHant: audioCodes.includes("zh-Hant") || captionCodes.includes("zh-Hant")
  });
}

const report = {
  href: research && research.href,
  title: research && research.title,
  chip: research && research.chip,
  player: research && research.player,
  button: research && research.button,
  audioNow: research && research.audioNow,
  availableAudio: research && research.availableAudio,
  captionNow: research && research.captionNow,
  tracklist: research && research.tracklist,
  translationCount: research && research.translationCount,
  adaptiveAudio: research && research.adaptiveAudio,
  playerCaptionCodes: research && research.playerCaptionCodes,
  playerTranslationCodes: research && research.playerTranslationCodes,
  resolvedAuto: research && research.resolvedAuto,
  cases,
  logs: session.logs
};

const out = join(here, "live-resolve.json");
writeFileSync(out, JSON.stringify(report, null, 2));
console.log(JSON.stringify({
  href: report.href,
  chip: report.chip,
  player: report.player,
  audioNow: report.audioNow,
  audioCodes: (report.availableAudio || []).map((item) => item && item.code),
  adaptiveAudio: (report.adaptiveAudio || []).map((item) => item.id + " " + item.displayName),
  captionNow: report.captionNow,
  tracklist: (report.tracklist || []).map((item) => item && item.code),
  playerCaptionCodes: report.playerCaptionCodes,
  translationCount: Array.isArray(report.playerTranslationCodes) ? report.playerTranslationCodes.length : report.translationCount,
  auto: report.resolvedAuto && report.resolvedAuto.final,
  detected: report.resolvedAuto && report.resolvedAuto.detected,
  availableAudioCount: report.resolvedAuto && report.resolvedAuto.available.audio.length,
  availableCaptionCount: report.resolvedAuto && report.resolvedAuto.available.captions.length,
  cases: (report.cases || []).map((item) => ({
    name: item.name,
    final: item.result && item.result.resolved && item.result.resolved.final,
    live: item.result && { audio: item.result.liveAudio, sub: item.result.liveCaption, on: item.result.captionsOn },
    hasBn: item.hasBn,
    hasRu: item.hasRu,
    hasZhHans: item.hasZhHans,
    captionCount: item.captionCount
  }))
}, null, 2));
console.log("wrote", out);
try { session.ws.close(); } catch {}
setTimeout(() => process.exit(0), 200);
