import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";
import vm from "node:vm";

const here = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(join(here, "..", "..", "extension", "background.js"), "utf8");
let now = 100;
const noopEvent = { addListener() {} };
const context = {
  URL,
  URLSearchParams,
  Date: { now: () => now },
  console: { log() {} },
  setTimeout() {},
  chrome: {
    runtime: { onMessage: noopEvent, getURL: (path) => path },
    storage: { sync: { get() {} } },
    tabs: {
      onUpdated: noopEvent,
      onRemoved: noopEvent,
      get() {},
      query() {},
      sendMessage() {},
      create() {}
    },
    scripting: { executeScript() {} },
    webRequest: { onCompleted: noopEvent }
  }
};
vm.createContext(context);
vm.runInContext(source, context);

const page = "https://kick.com/rampagejackson/videos/01a03b08-3dd8-75b7-9841-e43302bd010c?t=30529";
const vod = "https://stream.kick.com/archive/2026/8/29/media/hls/master.m3u8";
const ad = "https://cdn.example/advert/bumper.mp4";
const extensionless = "https://media.example.test/playback/asset?id=42";
const genericPage = "https://movies.example/watch/episode";
const protectedManifest = "https://fastplay.mom/manifests/episode/master.txt?verify=123-proof";
const protectedPlayer = "https://fastplay.mom/video/episode";

assert.ok(context.usesPageCatalog(page), "Kick VOD always uses the official page resolver");
assert.ok(context.usesPageCatalog("https://www.dailymotion.com/video/xap6qz2"));
assert.equal(
  context.embeddedPlayerPage({
    watchUrl: "https://rapidvid.net/vod/v1x7c5739a3",
    url: "https://rapidvid.net/vod/v1x7c5739a3",
    pageUrl: "https://www.fullhdfilmizlesene.now/film/supergirl/"
  }),
  "https://rapidvid.net/vod/v1x7c5739a3",
  "a lazy player iframe wins over unrelated network media"
);
assert.equal(
  context.embeddedPlayerPage({
    watchUrl: "https://dizipal2121.com/bolum/test",
    url: "",
    pageUrl: "https://dizipal2121.com/bolum/test",
    hasPreroll: true,
    duration: 2
  }),
  "https://dizipal2121.com/bolum/test",
  "a preroll-only surface resolves the containing player page"
);
assert.ok(context.looksProtectedMedia(protectedManifest));
assert.equal(
  context.embeddedPlayerPage({
    watchUrl: protectedManifest,
    url: protectedManifest,
    pageUrl: protectedPlayer
  }),
  protectedPlayer,
  "a proof-protected manifest is reopened through its HTML player"
);
const protectedProtocol = new URL(context.protocol({
  watchUrl: protectedManifest,
  url: protectedManifest,
  pageUrl: protectedPlayer,
  kind: "vod"
}, true));
assert.equal(
  protectedProtocol.searchParams.get("url"),
  protectedPlayer,
  "the custom protocol never transfers a protected manifest without its request proof"
);
const prerollOnly = {
  watchUrl: "https://dizipal2121.com/bolum/test",
  url: "",
  pageUrl: "https://dizipal2121.com/bolum/test",
  hasPreroll: true,
  duration: 2
};
assert.equal(
  context.betterInfo(null, prerollOnly, 12),
  prerollOnly,
  "popup sniffing preserves a page resolver candidate even without direct media"
);

context.rememberPlaying(7, { url: vod, duration: 86794, playingNow: true }, page, 0);
assert.equal(context.recentPlaying(7, page).url, vod, "the active frame survives until Open is clicked");
assert.equal(
  context.recentPlaying(7, page.replace("rampagejackson", "someone-else")),
  null,
  "playing media never crosses top-level pages"
);

context.rememberPlaying(8, { url: ad, duration: 2, playingNow: true }, "https://dizipal2121.com/bolum/test", 3);
assert.equal(context.recentPlaying(8, "https://dizipal2121.com/bolum/test"), null, "a short preroll is rejected");
assert.ok(context.tabSkipped(8).has(ad), "a rejected preroll cannot win through webRequest later");
assert.equal(
  context.isPrerollInfo({ url: "https://media.example/short-film.mp4", duration: 30, pageUrl: "https://media.example/watch" }),
  false,
  "legitimate short-form videos are not rejected merely for being under 90 seconds"
);

context.rememberNetwork(9, vod, genericPage);
context.rememberNetwork(
  11,
  extensionless,
  genericPage,
  [{ name: "Content-Type", value: "application/vnd.apple.mpegurl" }],
  "https://rapidvid.net/vod/player-42"
);
now += 5 * 60 * 1000;
assert.ok([vod, extensionless].includes(context.latestNetwork(9, genericPage).url), "an MSE manifest remains available after eight seconds");
assert.equal(
  context.latestNetwork(11, genericPage).referrer,
  "https://rapidvid.net/vod/player-42",
  "nested-player media retains the iframe referer needed by the CDN"
);
assert.ok(context.looksMediaResponse(extensionless, [{ name: "content-type", value: "application/dash+xml; charset=utf-8" }]));
assert.ok(!context.looksMediaResponse("https://media.example/segment-12", [{ name: "content-type", value: "video/mp4" }]));
now += 6 * 60 * 1000;
assert.equal(context.latestNetwork(9, genericPage), null, "network candidates still expire");

const weak = { url: "https://cdn.example/video.mp4", duration: 0 };
const playing = { url: vod, duration: 86794, playingNow: true, reportedPlaying: true };
assert.equal(context.betterInfo(weak, playing, 10), playing, "active long-form playback beats an arbitrary MP4");

const listed = new URL(context.protocol({
  url: "https://cdn.example/hls/master.m3u8",
  pageUrl: "https://dizipal2121.com/bolum/x",
  kind: "vod",
  captionTracks: [
    { code: "en", url: "https://cdn.example/en.vtt", name: "English" },
    { code: "tr", url: "https://cdn.example/tr.vtt", name: "Turkish" }
  ]
}, true));
assert.equal(listed.searchParams.get("url"), "https://cdn.example/hls/master.m3u8");
assert.deepEqual(listed.searchParams.getAll("cap"), [
  "en|https://cdn.example/en.vtt|English",
  "tr|https://cdn.example/tr.vtt|Turkish"
], "Open transfers every available sidecar caption");
assert.equal(listed.searchParams.get("sub"), null, "generic Open does not send the site's current subtitle");
assert.equal(listed.searchParams.get("audio"), null, "generic Open does not send the site's current dub");

assert.ok(context.usesPageCatalog("https://www.dailymotion.com/video/xap6qz2"));
assert.ok(
  !context.usesPageCatalog("https://cdndirector.dailymotion.com/cdn/manifest/video/xap6qz2.m3u8?sec=token"),
  "Dailymotion CDN manifests are not catalog pages"
);
const daily = new URL(context.protocol({
  watchUrl: "https://cdndirector.dailymotion.com/cdn/manifest/video/xap6qz2.m3u8?sec=token",
  url: "https://cdndirector.dailymotion.com/cdn/manifest/video/xap6qz2.m3u8?sec=token",
  pageUrl: "https://www.dailymotion.com/video/xap6qz2",
  kind: "vod"
}, true));
assert.equal(
  daily.searchParams.get("url"),
  "https://www.dailymotion.com/video/xap6qz2",
  "Dailymotion Open keeps the watch page so the catalog can keep the signed master"
);
const close = new URL(context.protocol({
  url: "https://hls8.playmix.uno/hls/film.mp4/master.txt",
  watchUrl: "https://hdfilmcehennemi.mobi/video/embed/xnZQ9xsXLfb/?rapidrame_id=gr2rb77x3mpm",
  pageUrl: "https://www.hdfilmcehennemi.nl/somebody-2024-hdf/",
  kind: "vod"
}, true));
assert.equal(
  close.searchParams.get("url"),
  "https://hdfilmcehennemi.mobi/video/embed/xnZQ9xsXLfb/?rapidrame_id=gr2rb77x3mpm",
  "Close/Rapidrame Open sends the HTML player, not a playmix manifest"
);

const youtube = new URL(context.protocol({
  watchUrl: "https://www.youtube.com/watch?v=dQw4w9wgBcQ",
  url: "https://www.youtube.com/watch?v=dQw4w9wgBcQ",
  kind: "vod",
  audio: "tr",
  sub: "tr",
  captionUrl: "https://www.youtube.com/api/timedtext?v=dQw4w9wgBcQ&lang=tr",
  captionTracks: [
    { code: "tr", url: "https://www.youtube.com/api/timedtext?v=dQw4w9wgBcQ&lang=tr", name: "Turkish" }
  ]
}, true));
assert.equal(youtube.searchParams.get("audio"), "tr");
assert.equal(youtube.searchParams.get("sub"), "tr");
assert.match(youtube.searchParams.get("caption"), /timedtext/);
assert.equal(
  youtube.searchParams.get("cap"),
  "tr|https://www.youtube.com/api/timedtext?v=dQw4w9wgBcQ&lang=tr|Turkish"
);

const bare = new URL(context.protocol({
  url: "https://cdn.example/hls/master.m3u8",
  pageUrl: "https://dizipal2121.com/bolum/x",
  kind: "vod",
  captionTracks: [{ code: "en", url: "https://cdn.example/subs/english", name: "English" }]
}, true));
assert.equal(
  bare.searchParams.get("cap"),
  "en|https://cdn.example/subs/english|English",
  "labeled sidecar URLs transfer even without a .vtt suffix"
);

console.log("background.test.mjs ok");
