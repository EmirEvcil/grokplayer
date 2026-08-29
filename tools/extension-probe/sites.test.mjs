import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";
import vm from "node:vm";

const here = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(join(here, "..", "..", "extension", "content", "sniff.js"), "utf8");

function el(tag, attrs, children) {
  const node = {
    tagName: tag.toUpperCase(),
    className: attrs.className || "",
    id: attrs.id || "",
    title: attrs.title || "",
    src: attrs.src || "",
    currentSrc: attrs.currentSrc || attrs.src || "",
    paused: attrs.paused !== false,
    muted: !!attrs.muted,
    currentTime: attrs.currentTime || 0,
    clientWidth: attrs.w || 640,
    clientHeight: attrs.h || 360,
    videoWidth: attrs.w || 640,
    videoHeight: attrs.h || 360,
    isConnected: true,
    parentElement: null,
    children: children || [],
    getAttribute(name) {
      return attrs[name] || "";
    },
    getBoundingClientRect() {
      return {
        width: this.clientWidth,
        height: this.clientHeight,
        top: 10,
        left: 10,
        right: 10 + this.clientWidth,
        bottom: 10 + this.clientHeight
      };
    },
    querySelector() {
      return null;
    },
    querySelectorAll() {
      return [];
    }
  };
  (children || []).forEach((child) => {
    child.parentElement = node;
  });
  return node;
}

function load(doc, href) {
  const parsed = new URL(href);
  const context = {
    window: { innerWidth: 1280, innerHeight: 800 },
    chrome: null,
    URL,
    location: { href: parsed.href, hostname: parsed.hostname, pathname: parsed.pathname },
    document: doc,
    performance: { getEntriesByType: () => [] }
  };
  vm.createContext(context);
  vm.runInContext(source, context);
  return context.window.GrokPlayerSniff;
}

const api = load({ querySelectorAll: () => [] }, "https://example.com/");
assert.ok(!api.looksMedia("https://scontent.cdninstagram.com/v/t51.2885-15/photo.jpg"));
assert.ok(api.looksMedia("https://scontent.cdninstagram.com/o1/v/t16/f2/m86/clip"));
assert.ok(api.looksAd("https://pubads.g.doubleclick.net/preroll.mp4"));
assert.ok(!api.looksAd("https://stream.kick.com/ivs/master.m3u8"));

const kickVideo = el("video", { src: "https://stream.kick.com/live.m3u8", w: 960, h: 540, paused: true });
const kickApi = load({
  querySelectorAll(sel) {
    return sel === "iframe" ? [] : [kickVideo];
  }
}, "https://kick.com/naru");
assert.equal(kickApi.primarySurfaces()[0], kickVideo);
const kickSniff = kickApi();
assert.equal(kickSniff.kind, "live");
assert.equal(kickSniff.url, "https://kick.com/naru");
assert.equal(kickSniff.sources.length, 0);

const rumblePreview = el("video", { src: "https://cdn.rumble.cloud/video/8v2sa.faa.mp4", w: 800, h: 450, paused: false, currentTime: 1 });
const rumblePreviewApi = load({
  querySelectorAll(sel) {
    return sel === "iframe" ? [] : [rumblePreview];
  }
}, "https://rumble.com/v7elrde-x.html");
assert.equal(
  rumblePreviewApi().url,
  "https://rumble.com/v7elrde-x.html",
  "a Rumble preview clip is not transferred as the video"
);
const rumbleHls = "https://rumble.com/hls-vod/v7elrde/playlist.m3u8";
const rumblePlaying = el("video", { src: rumbleHls, w: 800, h: 450, paused: false, currentTime: 1 });
const rumbleHlsApi = load({
  querySelectorAll(sel) {
    return sel === "iframe" ? [] : [rumblePlaying];
  }
}, "https://rumble.com/v7elrde-x.html");
const rumbleHlsSniff = rumbleHlsApi();
assert.equal(rumbleHlsSniff.url, rumbleHls, "Rumble VOD transfers the captured HLS");
assert.ok(api.looksAd("https://dmxleo.dailymotion.com/cdn/manifest/video/xb23uyu.m3u8"));

const frame = el("iframe", { src: "https://p.playturka.space/#zDEnwTc0", w: 800, h: 450, className: "w-full h-full" });
const box = el("div", { className: "group/player aspect-video", w: 800, h: 450 }, [frame]);
const filmApi = load({
  querySelectorAll(sel) {
    return sel === "iframe" ? [frame] : [box, frame];
  }
}, "https://filmizlehell.net/film/1750-katil-makine-izle");
assert.equal(filmApi.primarySurfaces().length, 0, "no chip until the embed is playing");
filmApi.noteChildPlaying();
assert.equal(filmApi.primarySurfaces()[0], frame);

console.log("sites.test.mjs ok");
