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
    src: attrs.src || "",
    currentSrc: attrs.currentSrc || attrs.src || "",
    href: attrs.href || "",
    paused: attrs.paused !== false,
    muted: !!attrs.muted,
    currentTime: attrs.currentTime || 0,
    isConnected: true,
    clientWidth: attrs.w || 640,
    clientHeight: attrs.h || 360,
    videoWidth: attrs.w || 640,
    videoHeight: attrs.h || 360,
    parentElement: null,
    shadowRoot: attrs.shadowRoot || null,
    children: children || [],
    getAttribute(name) {
      return attrs[name] || attrs[name.replace("data-", "")] || "";
    },
    getBoundingClientRect() {
      const top = attrs.top != null ? attrs.top : 10;
      const left = attrs.left != null ? attrs.left : 10;
      return {
        width: this.clientWidth,
        height: this.clientHeight,
        top,
        left,
        right: left + this.clientWidth,
        bottom: top + this.clientHeight
      };
    },
    querySelector(sel) {
      return this.querySelectorAll(sel)[0] || null;
    },
    querySelectorAll(sel) {
      const want = sel.split(",").map((item) => item.trim().replace(/\[.*\]/, "").toLowerCase());
      const all = [];
      const walk = (item) => {
        all.push(item);
        (item.children || []).forEach(walk);
      };
      (this.children || []).forEach(walk);
      if (sel === "video, *" || sel === "*") {
        return all;
      }
      return all.filter((item) => want.includes(item.tagName.toLowerCase()) || want.some((token) => token.startsWith(".") && item.className.includes(token.slice(1))));
    },
    closest() {
      return this.parentElement;
    },
    getRootNode() {
      return { host: this.shadowHost || null };
    }
  };
  (children || []).forEach((child) => {
    child.parentElement = node;
  });
  return node;
}

function load(doc, perf, href) {
  const parsed = new URL(href || "https://example.com/watch");
  const context = {
    window: { innerWidth: 1280, innerHeight: 800 },
    chrome: null,
    URL,
    location: { href: parsed.href, hostname: parsed.hostname, pathname: parsed.pathname },
    document: doc,
    performance: { getEntriesByType: () => perf || [] }
  };
  vm.createContext(context);
  vm.runInContext(source, context);
  return context.window.GrokPlayerSniff;
}

const ad = "https://pubads.g.doubleclick.net/preroll.mp4";
const rekla = "https://www.hdfilmcehennemi.nl/rekla/luxyenii.mp4";
const movie = "https://cdn.film/movie/master.m3u8";
const trailer = "https://cdn.other/widget/clip.mp4";
const rumbleHls = "https://rumble.com/hls-vod/abc/playlist.m3u8";
const rumbleMp4 = "https://cdn.rumble.cloud/video/8v2sa.faa.mp4";
const hlsTxt = "https://hls8.playmix.uno/hls/filmakinesimp4-f9gx1M12BwC.mp4/master.txt";

const mainVideo = el("video", { src: movie, w: 960, h: 540, paused: false, currentTime: 12 });
const adVideo = el("video", { src: ad, w: 300, h: 170 });
const sideVideo = el("video", { src: trailer, w: 240, h: 140 });
const mainPlayer = el("div", { className: "jwplayer movie-player", id: "player" }, [mainVideo]);
const adPlayer = el("div", { className: "ad-player" }, [adVideo]);
const sidePlayer = el("div", { className: "sidebar-player" }, [sideVideo]);
const body = el("body", {}, [mainPlayer, adPlayer, sidePlayer]);
mainPlayer.parentElement = body;
adPlayer.parentElement = body;
sidePlayer.parentElement = body;
const doc = {
  title: "Movie",
  body,
  documentElement: body,
  querySelectorAll(sel) {
    if (sel === "iframe") {
      return [];
    }
    if (sel === "video, *") {
      return [mainPlayer, mainVideo, adPlayer, adVideo, sidePlayer, sideVideo];
    }
    if (sel === "video") {
      return [mainVideo, adVideo, sideVideo];
    }
    return [];
  }
};

const api = load(doc, [
  { name: movie },
  { name: ad },
  { name: trailer },
  { name: rumbleHls },
  { name: rumbleMp4 }
], "https://www.hdfilmcehennemi.nl/the-last-scene-2026/");

const tiktokCdn = "https://v16-webapp.tiktokcdn.com/video/tos/useast2a/foo?mime_type=video_mp4";
const instagramCdn = "https://scontent.cdninstagram.com/o1/v/t16/f2/m86/foo";
assert.ok(api.looksMedia(tiktokCdn), "TikTok CDN URLs have no file extension");
assert.ok(api.looksMedia(instagramCdn), "Instagram video CDN URLs have no file extension");
assert.ok(api.looksImage("https://scontent.cdninstagram.com/v/t51.2885-15/foo.jpg"));
assert.ok(!api.looksMedia("https://scontent.cdninstagram.com/v/t51.2885-15/foo.jpg"), "Instagram photos are not streams");
assert.ok(api.mediaScore(tiktokCdn) > 0);
assert.ok(
  api.mediaScore(instagramCdn) >
  api.mediaScore(instagramCdn + "?bytestart=800000&byteend=900000"),
  "Instagram byte-range fragments must lose to the full reel"
);
assert.equal(
  api.pickTransfer(
    instagramCdn + "?bytestart=800000&byteend=900000",
    [{ url: instagramCdn + "?bytestart=800000&byteend=900000" }],
    "https://www.instagram.com/reels/DbMyk2ih57c/",
    true,
    false
  ).includes("bytestart"),
  false,
  "Instagram transfers the full reel URL, not a byte range"
);
assert.ok(api.looksAd(ad));
assert.ok(api.looksAd(rekla), "site-hosted /rekla/ bumpers are ads");
assert.ok(!api.looksAd(movie));
assert.ok(!api.looksAd(hlsTxt));
assert.ok(api.looksMedia(hlsTxt));
assert.ok(api.looksImageList("https://cdn/hls/film.mp4/txt/master.txt"));
assert.ok(!api.looksMedia("https://cdn/hls/film.mp4/image000.jpg"));
assert.ok(api.mediaScore(hlsTxt) > api.mediaScore(movie.replace("master.m3u8", "360.mp4")));
assert.ok(api.mediaScore(rumbleMp4) < api.mediaScore(rumbleHls));
assert.equal(api.pickPrimary([ad, rekla, movie, trailer]), movie);
assert.equal(api.pickPrimary([rumbleMp4, rumbleHls]), rumbleHls);
assert.equal(api.pickPrimary([rekla, ad]), "");

const mainSources = api.sourcesFor(mainVideo);
assert.equal(mainSources[0].name, "Main");
assert.equal(mainSources[0].url, movie);
assert.ok(!mainSources.some((item) => item.url === trailer), "sidebar clip must not leak into the movie player");
assert.ok(!mainSources.some((item) => item.url === ad), "preroll must not be listed as a movie source");

const sideSources = api.sourcesFor(sideVideo);
assert.equal(sideSources[0].url, trailer);
assert.ok(!sideSources.some((item) => item.url === movie), "movie HLS must not leak into the sidebar player");

assert.equal(api.primarySurfaces().length, 1, "playing watch video is detected without a click");
assert.equal(api.primarySurfaces()[0], mainVideo);
assert.ok(api.isDedicatedPlayer(mainVideo));
assert.ok(api.isAdSurface(adVideo), "ad containers stay marked");

const hoverVideo = el("video", { src: movie, w: 180, h: 120, muted: true, paused: false, currentTime: 1, className: "hover-card" });
const hoverWrap = el("div", { className: "hover-card" }, [hoverVideo]);
hoverVideo.parentElement = hoverWrap;
hoverWrap.parentElement = body;
const fypDoc = {
  title: "TikTok",
  body,
  documentElement: body,
  querySelectorAll(sel) {
    if (sel === "iframe") {
      return [];
    }
    if (sel === "video, *") {
      return [mainPlayer, mainVideo, hoverWrap, hoverVideo];
    }
    if (sel === "video") {
      return [mainVideo, hoverVideo];
    }
    return [];
  }
};
const fypApi = load(fypDoc, [{ name: movie }], "https://www.tiktok.com/foryou");
assert.ok(fypApi.isFeedPreview(hoverVideo), "TikTok hover previews are still classified");
assert.ok(!fypApi.primarySurfaces().includes(hoverVideo), "hover previews must not get a chip");
assert.equal(fypApi.primarySurfaces()[0], mainVideo);
assert.ok(fypApi.isDedicatedPlayer(mainVideo));
assert.ok(!fypApi.isDedicatedPlayer(hoverVideo));

const watchApi = load(doc, [{ name: movie }], "https://www.tiktok.com/@x/video/7676616845960531221");
assert.equal(watchApi.primarySurfaces()[0], mainVideo);

const tiktokCdnPlay = "https://v16-webapp.tiktokcdn.com/video/tos/useast2a/foo?mime_type=video_mp4";
assert.equal(
  watchApi.pickTransfer(tiktokCdnPlay, [{ url: tiktokCdnPlay }], "https://www.tiktok.com/@x/video/1", true, false),
  tiktokCdnPlay,
  "the playing TikTok CDN URL is what we transfer"
);
assert.equal(
  api.pickTransfer(ad, [{ url: ad }, { url: movie }], "https://www.hdfilmcehennemi.nl/the-last-scene-2026/", false, false),
  movie,
  "ads are skipped when the main media is already known"
);
api.skipUrl(ad);
assert.equal(api.pickPrimary([ad, movie]), movie);

const adOnlyVideo = el("video", { src: rekla, w: 960, h: 540, paused: false, currentTime: 3 });
const adOnlyPlayer = el("div", { className: "jwplayer movie-player" }, [adOnlyVideo]);
adOnlyVideo.parentElement = adOnlyPlayer;
const adOnlyDoc = {
  title: "Film",
  querySelectorAll(sel) {
    if (sel === "iframe") {
      return [];
    }
    if (sel === "video, *") {
      return [adOnlyPlayer, adOnlyVideo];
    }
    return [adOnlyVideo];
  }
};
const hdfilmOnlyAds = load(adOnlyDoc, [{ name: rekla }, { name: ad }], "https://www.hdfilmcehennemi.nl/the-last-scene-2026/");
const hdfilmSniff = hdfilmOnlyAds();
assert.equal(hdfilmSniff.url, "https://www.hdfilmcehennemi.nl/the-last-scene-2026/");
assert.ok(!hdfilmSniff.url.includes("/rekla/"));

const embed = el("iframe", { className: "close", "data-src": "https://hdfilmcehennemi.mobi/video/embed/Tr4Yz605cMT/?rapidrame_id=x", w: 800, h: 450 });
const playerBox = el("div", { className: "player-container video-player-container-here", w: 800, h: 450 }, [embed]);
embed.parentElement = playerBox;
const embedDoc = {
  title: "Film",
  querySelectorAll(sel) {
    if (sel === "iframe") {
      return [embed];
    }
    return [];
  }
};
const embedApi = load(embedDoc, [{ name: rekla }], "https://www.hdfilmcehennemi.nl/the-last-scene-2026/");
assert.equal(embedApi.primarySurfaces().length, 0, "hdfilm must not show a chip before the film is playing");
embedApi.noteChildPlaying();
assert.equal(embedApi.primarySurfaces()[0], embed);

const pausedClip = el("video", { src: movie, w: 960, h: 540, paused: true, currentTime: 0 });
const otherApi = load({
  title: "Other",
  querySelectorAll(sel) {
    if (sel === "iframe") {
      return [];
    }
    if (sel === "video, *") {
      return [pausedClip];
    }
    return [pausedClip];
  }
}, [], "https://filmizlehell.net/film/1750-katil-makine-izle");
assert.equal(otherApi.primarySurfaces().length, 0, "other sites wait for playing media");
pausedClip.paused = false;
pausedClip.currentTime = 2;
assert.equal(otherApi.primarySurfaces()[0], pausedClip);

const kickPaused = el("video", { src: movie, w: 960, h: 540, paused: true, currentTime: 0 });
const kickApi = load({
  title: "Kick",
  querySelectorAll(sel) {
    if (sel === "iframe") {
      return [];
    }
    if (sel === "video, *") {
      return [kickPaused];
    }
    return [kickPaused];
  }
}, [], "https://kick.com/naru");
assert.equal(kickApi.primarySurfaces()[0], kickPaused, "Kick shows the chip when the player is detected");

const playturka = el("iframe", { src: "https://p.playturka.space/#zDEnwTc0", w: 800, h: 450, className: "w-full h-full border-none" });
const playBox = el("div", { className: "group/player aspect-video", w: 800, h: 450 }, [playturka]);
playturka.parentElement = playBox;
const filmDoc = {
  title: "Film",
  querySelectorAll(sel) {
    if (sel === "iframe") {
      return [playturka];
    }
    return sel.includes("video") ? [playBox, playturka] : [];
  }
};
const filmApi = load(filmDoc, [], "https://filmizlehell.net/film/1750-katil-makine-izle");
assert.equal(filmApi.primarySurfaces().length, 0, "filmizlehell chip waits until the embed plays");
filmApi.noteChildPlaying();
assert.equal(filmApi.primarySurfaces()[0], playturka);

assert.equal(api.pageKind("https://kick.com/rootthegamer"), "live");
assert.equal(api.pageKind("https://kick.com/rootthegamer/videos/abc"), "vod");
assert.equal(api.pageKind("https://rumble.com/v7elrde-x.html"), "vod");

const live = load(doc, [{ name: "https://kick.com/live.m3u8" }], "https://kick.com/rootthegamer");
const liveSniff = live();
assert.equal(liveSniff.kind, "live");
assert.equal(liveSniff.url, "https://kick.com/rootthegamer");
assert.equal(liveSniff.sources.length, 0);

const rumbleDoc = {
  title: "Rumble",
  querySelectorAll: (sel) => sel === "iframe" ? [] : sel.includes("video") ? [el("video", { src: rumbleHls, w: 800, h: 450 })] : []
};
const rumbleVideo = rumbleDoc.querySelectorAll("video")[0];
const rumbleWrap = el("div", { className: "media-player" }, [rumbleVideo]);
rumbleVideo.parentElement = rumbleWrap;
rumbleDoc.querySelectorAll = (sel) => {
  if (sel === "iframe") return [];
  if (sel === "video, *") return [rumbleWrap, rumbleVideo];
  return [rumbleVideo];
};
const rumbleApi = load(rumbleDoc, [{ name: rumbleHls }, { name: rumbleMp4 }], "https://rumble.com/v7elrde-x.html");
const rumbleSources = rumbleApi.sourcesFor(rumbleVideo);
assert.equal(rumbleSources[0].url, rumbleHls);
assert.equal(rumbleSources[0].name, "Main");
assert.equal(
  rumbleApi.pickTransfer(rumbleMp4, [{ url: rumbleMp4 }, { url: rumbleHls }], "https://rumble.com/v7elrde-x.html", true, false),
  rumbleHls,
  "Rumble VOD transfers the HLS, not a 10s preview"
);
assert.equal(
  live.pickTransfer("https://cdn.kick/clip.mp4", [{ url: "https://cdn.kick/clip.mp4" }], "https://kick.com/rootthegamer", true, true),
  "https://kick.com/rootthegamer",
  "Kick live always transfers the channel page"
);
const kickVodHls = "https://stream.kick.com/ivs/v1/196233775518/UTurJDh1l4q7/media/hls/master.m3u8";
const kickVodPage = "https://kick.com/kalatay3/videos/01a044ef-8900-7c3d-9539-696d32367f14";
const kickVodApi = load(doc, [{ name: kickVodHls }], kickVodPage);
assert.equal(kickVodApi.pageKind(kickVodPage), "vod");
assert.equal(
  kickVodApi.pickTransfer(kickVodHls, [{ url: kickVodHls }], kickVodPage, true, false),
  kickVodHls,
  "Kick VOD transfers the captured HLS"
);

const igFirst = "https://scontent.cdninstagram.com/o1/v/t16/reel-one";
const igSecond = "https://scontent.cdninstagram.com/o1/v/t16/reel-two";
const igPrev = el("video", { src: igFirst, w: 420, h: 740, paused: true, currentTime: 41, top: 10 });
const igNow = el("video", { src: "blob:https://www.instagram.com/abc", w: 420, h: 740, paused: false, currentTime: 2, top: 10 });
const igWrap = el("div", { className: "reel" }, [igPrev, igNow]);
igPrev.parentElement = igWrap;
igNow.parentElement = igWrap;
const igDoc = {
  title: "Instagram",
  querySelectorAll(sel) {
    if (sel === "iframe") {
      return [];
    }
    if (sel === "video, *") {
      return [igWrap, igPrev, igNow];
    }
    return [igPrev, igNow];
  }
};
const igApi = load(igDoc, [{ name: igFirst }, { name: igSecond }], "https://www.instagram.com/reels/DbMyk2ih57c/");
assert.equal(igApi.primarySurfaces()[0], igNow, "the chip follows the reel that is actually playing");
assert.ok(igApi.isActivePlayback(igNow));
assert.ok(!igApi.isActivePlayback(igPrev));
const igSniff = igApi(igNow);
assert.equal(igSniff.url, igSecond, "a blob reel transfers the latest unused Instagram CDN, not the first reel");

const igAud1 = "https://scontent.cdninstagram.com/o1/v/t16/a1?mime_type=audio_mp4";
const igVid1 = "https://scontent.cdninstagram.com/o1/v/t16/v1?mime_type=video_mp4";
const igAud2 = "https://scontent.cdninstagram.com/o1/v/t16/a2?mime_type=audio_mp4";
const igVid2 = "https://scontent.cdninstagram.com/o1/v/t16/v2?mime_type=video_mp4";
assert.ok(igApi.looksAudioOnly(igAud1));
assert.ok(!igApi.looksAudioOnly(igVid1));
assert.ok(igApi.mediaScore(igVid1) > igApi.mediaScore(igAud1));
const blobOne = el("video", { src: "blob:https://www.instagram.com/1", w: 420, h: 740, paused: true, currentTime: 8, top: 10 });
const blobTwo = el("video", { src: "blob:https://www.instagram.com/2", w: 420, h: 740, paused: false, currentTime: 1, top: 10 });
const blobDoc = {
  title: "Instagram",
  querySelectorAll(sel) {
    if (sel === "iframe") {
      return [];
    }
    if (sel === "video, *") {
      return [blobOne, blobTwo];
    }
    return [blobOne, blobTwo];
  }
};
const timedIg = load(blobDoc, [
  { name: igAud1, responseEnd: 100 },
  { name: igVid1, responseEnd: 110 },
  { name: igAud2, responseEnd: 500 },
  { name: igVid2, responseEnd: 510 }
], "https://www.instagram.com/reels/DbMyk2ih57c/");
assert.equal(timedIg.markPlayed(blobOne, 120), igVid1, "first reel binds the video fetched when it played");
assert.equal(timedIg(blobOne).url, igVid1);
assert.equal(timedIg(blobOne).audioUrl, igAud1, "audio-only is a sidecar, not the picture");
assert.equal(timedIg.markPlayed(blobTwo, 520), igVid2, "second reel binds its own later video");
assert.equal(timedIg(blobTwo).url, igVid2);
assert.equal(timedIg(blobTwo).audioUrl, igAud2);

console.log("sniff.test.mjs ok");
