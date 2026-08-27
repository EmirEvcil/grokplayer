import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const langsSrc = readFileSync(join(here, "..", "..", "extension", "content", "langs.js"), "utf8");
const port = Number(process.env.GROK_CDP_PORT || 9335);

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

const targets = await fetch(`http://127.0.0.1:${port}/json/list`).then((res) => res.json());
const page = targets.find((item) => item.type === "page" && /watch\?v=Qtl8lJwbd4g/.test(item.url || "")) ||
  targets.find((item) => item.type === "page" && /youtube/.test(item.url || ""));
if (!page) {
  throw new Error("No YouTube tab on debug port " + port);
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
async function evaluate(expression) {
  const result = await send("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
  return result.result && result.result.result ? result.result.result.value : result.result?.value;
}

await send("Runtime.enable");
await evaluate(langsSrc);

const inspect = await evaluate(`(function(){
  const player = document.getElementById("movie_player");
  function safe(fn){ try { return fn(); } catch(e){ return { error: String(e && e.message || e) }; } }
  const audio = safe(() => player.getAudioTrack());
  return {
    options: safe(() => player.getOptions()),
    captionOptions: safe(() => player.getOptions("captions")),
    ccOptions: safe(() => player.getOptions("cc")),
    track: safe(() => player.getOption("captions", "track")),
    ccTrack: safe(() => player.getOption("cc", "track")),
    tracklistLen: safe(() => (player.getOption("captions", "tracklist") || []).length),
    translationLen: safe(() => (player.getOption("captions", "translationLanguages") || []).length),
    audioCaptionTracks: audio && audio.captionTracks ? audio.captionTracks.map((item) => ({
      languageCode: item.languageCode,
      kind: item.kind,
      isDefault: item.isDefault,
      name: item.displayName || item.languageName
    })) : null,
    captionsInitialState: audio && audio.captionsInitialState,
    keys: audio && Object.keys(audio)
  };
})()`);

async function pickCaption(code) {
  return evaluate(`(async function(){
    const player = document.getElementById("movie_player");
    const want = ${JSON.stringify(code)};
    const attempts = [];
    function state(){
      const button = document.querySelector(".ytp-subtitles-button");
      const video = document.querySelector("video");
      let track = null;
      try { track = player.getOption && player.getOption("captions", "track"); } catch(e) {}
      const snap = {
        getAudioTrack: player.getAudioTrack && player.getAudioTrack(),
        getAvailableAudioTracks: player.getAvailableAudioTracks && player.getAvailableAudioTracks() || [],
        captionTrack: track,
        captionTracklist: (player.getOption && player.getOption("captions", "tracklist")) || [],
        translationLanguages: (player.getOption && player.getOption("captions", "translationLanguages")) || [],
        captionsOn: !!(button && button.getAttribute("aria-pressed") === "true"),
        textTracks: video ? [...video.textTracks].map((item) => ({ language: item.language, label: item.label, mode: item.mode })) : [],
        playerCaptionTracks: (player.getPlayerResponse && player.getPlayerResponse().captions && player.getPlayerResponse().captions.playerCaptionsTracklistRenderer.captionTracks) || [],
        playerTranslationLanguages: (player.getPlayerResponse && player.getPlayerResponse().captions && player.getPlayerResponse().captions.playerCaptionsTracklistRenderer.translationLanguages) || []
      };
      return {
        button: button ? { pressed: button.getAttribute("aria-pressed"), label: button.getAttribute("aria-label") } : null,
        track,
        resolved: window.GrokPlayerLangs.resolve(snap, { audioPref: "auto", subPref: "auto" })
      };
    }
    try {
      if (player.loadModule) player.loadModule("captions");
    } catch(e) { attempts.push("loadModule " + e); }
    try {
      player.setOption && player.setOption("captions", "track", { languageCode: want });
      attempts.push("setOption captions.track");
    } catch(e) { attempts.push("setOption " + e); }
    await new Promise((r) => setTimeout(r, 400));
    let afterSet = state();
    if (afterSet.resolved.final.sub === want || afterSet.resolved.final.sub === want + ":asr") {
      return { ok: true, method: "setOption", attempts, afterSet };
    }
    try {
      player.setOption && player.setOption("cc", "track", { languageCode: want });
      attempts.push("setOption cc.track");
    } catch(e) { attempts.push("setOption cc " + e); }
    await new Promise((r) => setTimeout(r, 400));
    afterSet = state();
    if (afterSet.resolved.final.sub === want || afterSet.resolved.final.sub === want + ":asr") {
      return { ok: true, method: "setOption-cc", attempts, afterSet };
    }

    const settings = document.querySelector(".ytp-settings-button");
    if (settings) settings.click();
    await new Promise((r) => setTimeout(r, 250));
    const items = [...document.querySelectorAll(".ytp-menuitem")];
    const subItem = items.find((item) => /subtitle|caption|altyaz/i.test(item.textContent || ""));
    if (subItem) subItem.click();
    await new Promise((r) => setTimeout(r, 300));
    const langs = [...document.querySelectorAll(".ytp-menuitem")];
    const names = langs.map((item) => (item.textContent || "").trim());
    const target = langs.find((item) => {
      const text = (item.textContent || "").toLowerCase();
      return text.includes(want) ||
        (want === "ru" && /russian|русск/.test(text)) ||
        (want === "bn" && /bangla|bengali|বাংলা/.test(text)) ||
        (want === "zh-Hans" && /simplified|简体/.test(text)) ||
        (want === "tr" && /turkish|t[uü]rk/.test(text)) ||
        (want === "off" && /off|kapal/.test(text));
    });
    if (target) {
      target.click();
      attempts.push("menu " + (target.textContent || "").trim());
    } else {
      attempts.push("menu-miss " + names.slice(0, 20).join(" | "));
      if (settings) settings.click();
    }
    await new Promise((r) => setTimeout(r, 500));
    const afterMenu = state();
    return {
      ok: afterMenu.resolved.final.sub.replace(/:asr$/, "") === want || (want === "off" && !afterMenu.resolved.final.sub),
      method: "menu",
      attempts,
      names: names.slice(0, 40),
      afterMenu
    };
  })()`);
}

const results = {
  inspect,
  ru: await pickCaption("ru"),
  bn: await pickCaption("bn"),
  zh: await pickCaption("zh-Hans"),
  tr: await pickCaption("tr"),
  off: await pickCaption("off")
};

writeFileSync(join(here, "live-captions.json"), JSON.stringify(results, null, 2));
console.log(JSON.stringify({
  options: inspect && inspect.options,
  captionOptions: inspect && inspect.captionOptions,
  track: inspect && inspect.track,
  audioCaptionTracks: inspect && inspect.audioCaptionTracks,
  captionsInitialState: inspect && inspect.captionsInitialState,
  ru: results.ru && { ok: results.ru.ok, method: results.ru.method, final: results.ru.afterMenu && results.ru.afterMenu.resolved.final || results.ru.afterSet && results.ru.afterSet.resolved.final, attempts: results.ru.attempts, names: results.ru.names },
  bn: results.bn && { ok: results.bn.ok, method: results.bn.method, final: results.bn.afterMenu && results.bn.afterMenu.resolved.final || results.bn.afterSet && results.bn.afterSet.resolved.final, attempts: results.bn.attempts },
  zh: results.zh && { ok: results.zh.ok, method: results.zh.method, final: results.zh.afterMenu && results.zh.afterMenu.resolved.final || results.zh.afterSet && results.zh.afterSet.resolved.final },
  tr: results.tr && { ok: results.tr.ok, method: results.tr.method, final: results.tr.afterMenu && results.tr.afterMenu.resolved.final || results.tr.afterSet && results.tr.afterSet.resolved.final },
  off: results.off && { ok: results.off.ok, method: results.off.method, final: results.off.afterMenu && results.off.afterMenu.resolved.final || results.off.afterSet && results.off.afterSet.resolved.final }
}, null, 2));
try { ws.close(); } catch {}
setTimeout(() => process.exit(0), 200);
