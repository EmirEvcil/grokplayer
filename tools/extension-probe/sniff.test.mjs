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
    duration: attrs.duration || 0,
    ended: !!attrs.ended,
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

function load(doc, perf, href, now) {
  const parsed = new URL(href || "https://example.com/watch");
  let clock = now || 0;
  const context = {
    window: { innerWidth: 1280, innerHeight: 800 },
    chrome: null,
    URL,
    location: { href: parsed.href, hostname: parsed.hostname, pathname: parsed.pathname },
    document: doc,
    performance: {
      now: () => clock,
      getEntriesByType: () => perf || []
    }
  };
  vm.createContext(context);
  vm.runInContext(source, context);
  const api = context.window.GrokPlayerSniff;
  api.setNow = (value) => {
    clock = value;
  };
  return api;
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
assert.ok(api.looksMedia("https://i.collaborate.pics/cdn/down/abc/index.m3u8"));
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
assert.equal(api.looksShortAd("https://media.example/short-film.mp4", 30, "https://media.example/watch"), false);
assert.ok(!api.looksAd(hlsTxt));
assert.ok(api.looksMedia(hlsTxt));
assert.ok(api.looksClosePlaylist("https://cdn/hls/film.mp4/txt/master.txt"));
assert.ok(!api.looksMedia("https://cdn/hls/film.mp4/txt/master.txt"));
assert.ok(api.looksImageList("https://cdn/hls/film.mp4/txt/master.txt"));
assert.equal(
  api.pickTransfer(
    "https://cdn/hls/film.mp4/txt/master.txt",
    [{ url: "https://hls8.playmix.uno/hls/film.mp4/master.txt" }, { url: "https://cdn/hls/film.mp4/txt/master.txt" }],
    "https://www.hdfilmcehennemi.nl/the-last-scene-2026/",
    false,
    false,
    7200
  ),
  "https://hls8.playmix.uno/hls/film.mp4/master.txt",
  "Close JPEG playlist is not transferred when real HLS exists"
);
assert.ok(api.looksAd("https://i.marmorated.pics/v/5c45826a72d646cf5aecd1d3f500a4bb.mp4"));
assert.equal(
  api.pickTransfer(
    "https://i.marmorated.pics/v/ad.mp4",
    [{ url: "https://i.marmorated.pics/v/ad.mp4" }, { url: movie }],
    "https://dizipal2121.com/bolum/x",
    false,
    false,
    15
  ),
  movie,
  "dizipal preroll is not transferred"
);
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
assert.equal(hdfilmSniff.url, "", "ads-only pages do not transfer the HTML page");
assert.ok(!String(hdfilmSniff.url).includes("/rekla/"));

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
assert.equal(embedApi.primarySurfaces().length, 0, "hdfilm waits for the iframe to play");
embedApi.noteChildPlaying();
assert.equal(embedApi.primarySurfaces()[0], embed, "hdfilm chip appears once the player is playing");

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
assert.equal(filmApi.primarySurfaces().length, 0, "filmizlehell waits for the iframe to play");
filmApi.noteChildPlaying();
assert.equal(filmApi.primarySurfaces()[0], playturka, "filmizlehell chip appears once the player is playing");

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
const kickLiveHls = "https://fa723fc1b171.us-west-2.playback.live-video.net/api/video/v1/us-west-2.channel.m3u8";
const kickDatedHls = "https://stream.kick.com/3c81249a5ce0/ivs/v1/196233775518/jHtppgXXoKhP/2026/8/27/23/20/6IEGUcHLLWRa/media/hls/master.m3u8";
assert.equal(
  kickVodApi.pickTransfer(kickLiveHls, [{ url: kickLiveHls }, { url: kickDatedHls }], kickVodPage, true, false),
  kickDatedHls,
  "Kick VOD does not transfer the channel live playlist"
);
assert.ok(kickVodApi.looksKickLive(kickLiveHls));
assert.ok(kickVodApi.mediaScore(kickDatedHls) > kickVodApi.mediaScore(kickLiveHls));
assert.equal(
  kickVodApi.pickTransfer(
    "https://cdn.kick/clip12.mp4",
    [{ url: "https://cdn.kick/clip12.mp4" }, { url: kickDatedHls }],
    kickVodPage,
    true,
    false,
    12
  ),
  kickDatedHls,
  "Kick VOD does not transfer a 12s clip"
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

const exactIg = "https://instagram.fadb5-1.fna.fbcdn.net/o1/v/t2/f2/m86/exact-reel.mp4";
const otherIg = "https://scontent.cdninstagram.com/o1/v/t16/preloaded-other-reel";
const igBootDoc = {
  ...igDoc,
  scripts: [{
    textContent: JSON.stringify({
      require: [{
        data: {
          edges: [
            { node: { code: "other", video_versions: [{ url: otherIg }] } },
            { node: { code: "DcodW89iBRK", video_versions: [{ url: exactIg }] } }
          ]
        }
      }]
    })
  }]
};
const exactIgApi = load(igBootDoc, [{ name: otherIg }], "https://www.instagram.com/reels/DcodW89iBRK/");
assert.equal(exactIgApi.instagramPageMedia(), exactIg);
assert.equal(exactIgApi(igNow).url, exactIg, "a direct Reel URL never opens another preloaded feed video");

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

const closeJpeg = "https://cdn.example/hls/film.mp4/txt/master.txt";
assert.equal(api.siblingPlaylist(closeJpeg), "https://cdn.example/hls/film.mp4/master.txt");
assert.ok(api.looksMedia(api.siblingPlaylist(closeJpeg)));
assert.ok(!api.looksMedia(closeJpeg));

const episode = "https://cdn.film/episode/master.m3u8";
const preroll = "https://i.marmorated.pics/v/ad.mp4";
const dizipalBlob = el("video", {
  src: "blob:https://dizipal2121.com/x",
  w: 960,
  h: 540,
  paused: false,
  currentTime: 40,
  duration: 4140
});
const dizipalWrap = el("div", { className: "jwplayer" }, [dizipalBlob]);
dizipalBlob.parentElement = dizipalWrap;
const dizipalDoc = {
  title: "Episode",
  querySelectorAll(sel) {
    if (sel === "iframe") {
      return [];
    }
    if (sel === "video, *") {
      return [dizipalWrap, dizipalBlob];
    }
    return [dizipalBlob];
  }
};
const dizipalApi = load(dizipalDoc, [
  { name: preroll, responseEnd: 50 },
  { name: episode, responseEnd: 80 }
], "https://dizipal2121.com/bolum/yuzuklerin-efendisi-guc-yuzukleri-1-sezon-6-bolum");
dizipalApi.markPlayed(dizipalBlob, 40);
dizipalApi.setNow(20000);
assert.equal(dizipalApi.mediaForVideo(dizipalBlob), episode, "Open binds the episode, not the preroll");
assert.equal(dizipalApi.current().url, episode);
assert.equal(dizipalApi.current().playingNow, true);

const twoSec = "https://cdn.ads.example/bumper.mp4";
const hourShow = "https://cdn.film/episode-long/master.m3u8";
const bumper = el("video", {
  src: twoSec,
  w: 960,
  h: 540,
  paused: false,
  currentTime: 1,
  duration: 2,
  className: "preroll"
});
const bumperWrap = el("div", { className: "preroll-ad", id: "prerollAd" }, [bumper]);
bumper.parentElement = bumperWrap;
const longVid = el("video", {
  src: hourShow,
  w: 960,
  h: 540,
  paused: false,
  currentTime: 20,
  duration: 4140
});
const longWrap = el("div", { className: "jwplayer" }, [longVid]);
longVid.parentElement = longWrap;
const prerollDoc = {
  title: "Episode",
  querySelectorAll(sel) {
    if (sel === "iframe") {
      return [];
    }
    if (sel === "video, *") {
      return [bumperWrap, bumper, longWrap, longVid];
    }
    return [bumper, longVid];
  }
};
const prerollApi = load(prerollDoc, [{ name: twoSec }, { name: hourShow }], "https://dizipal2121.com/bolum/yuzuklerin-efendisi-guc-yuzukleri-1-sezon-6-bolum");
assert.ok(prerollApi.isPrerollVideo(bumper), "a 2s clip is detected as a preroll");
assert.ok(!prerollApi.isPrerollVideo(longVid));
assert.equal(prerollApi.primarySurfaces()[0], longVid, "the chip follows the long episode, not the preroll");
assert.equal(prerollApi.current().url, hourShow, "Open transfers the 1h09 episode, not a 2s bumper");
prerollApi.rememberShortMedia(twoSec, 2);
assert.equal(
  prerollApi.pickTransfer(twoSec, [{ url: twoSec }, { url: hourShow }], "https://dizipal2121.com/bolum/x", false, false, 4140),
  hourShow,
  "a known short bumper is never transferred"
);

const configuredPreroll = el("video", {
  src: preroll,
  w: 960,
  h: 540,
  paused: false,
  currentTime: 1,
  duration: 2,
  id: "prerollVideo"
});
const configuredAd = el("div", { className: "preroll-ad", id: "prerollAd" }, [configuredPreroll]);
const configuredPlayer = el("div", {
  className: "video-player-container",
  id: "videoContainer",
  "data-cfg": "encoded-real-player",
  w: 960,
  h: 540
}, [configuredAd]);
const configuredDoc = {
  title: "Configured episode",
  querySelectorAll(sel) {
    if (sel === "iframe") return [];
    if (sel === "video, *") return [configuredPlayer, configuredAd, configuredPreroll];
    if (sel.includes("[data-cfg]")) return [configuredPlayer];
    return [configuredPreroll];
  }
};
const configuredApi = load(configuredDoc, [{ name: preroll }], "https://dizipal2121.com/bolum/configured-episode");
assert.equal(configuredApi.primarySurfaces()[0], configuredPlayer, "a configured player wins while only its preroll video exists");
assert.equal(configuredApi.current().url, "", "the preroll URL is not transferred from a configured player");
assert.equal(configuredApi.current().watchUrl, "https://dizipal2121.com/bolum/configured-episode", "the desktop resolves the real player from the page config");

const kickClip = "https://stream.kick.com/ivs/clip12/media/hls/master.m3u8";
const kickVodLong = "https://stream.kick.com/3c81249a5ce0/ivs/v1/196233775518/jHtppgXXoKhP/2026/8/27/23/20/6IEGUcHLLWRa/media/hls/master.m3u8";
const kickBlob = el("video", {
  src: "blob:https://kick.com/vod",
  w: 960,
  h: 540,
  paused: false,
  currentTime: 30,
  duration: 5400
});
const kickWrap = el("div", { className: "player" }, [kickBlob]);
kickBlob.parentElement = kickWrap;
const kickVodDoc = {
  title: "Kick VOD",
  querySelectorAll(sel) {
    if (sel === "iframe") {
      return [];
    }
    if (sel === "video, *") {
      return [kickWrap, kickBlob];
    }
    return [kickBlob];
  }
};
const kickNow = load(kickVodDoc, [
  { name: kickClip, responseEnd: 20 },
  { name: kickVodLong, responseEnd: 40 }
], kickVodPage);
kickNow.markPlayed(kickBlob, 30);
assert.equal(kickNow.mediaForVideo(kickBlob), kickVodLong, "Kick VOD binds the dated HLS, not a 12s clip");
kickNow.setNow(25000);
assert.equal(
  kickNow.mediaForVideo(kickBlob),
  kickVodLong,
  "durationchange must not wipe the VOD bind and fall back to a clip"
);
assert.equal(kickNow.current().url, kickVodLong);
assert.equal(kickNow.current().playingNow, true);

const firstMovie = "https://cdn.film/one/master.m3u8";
const secondMovie = "https://cdn.film/two/master.m3u8";
const switched = el("video", {
  src: "blob:https://example.com/2",
  w: 960,
  h: 540,
  paused: false,
  currentTime: 8,
  duration: 7200
});
const switchWrap = el("div", { className: "jwplayer" }, [switched]);
switched.parentElement = switchWrap;
const switchDoc = {
  title: "Switch",
  querySelectorAll(sel) {
    if (sel === "iframe") {
      return [];
    }
    if (sel === "video, *") {
      return [switchWrap, switched];
    }
    return [switched];
  }
};
const switchApi = load(switchDoc, [{ name: firstMovie }], "https://example.com/one");
switchApi.markPlayed(switched, 10);
assert.equal(switchApi.current().url, firstMovie);
const switchApi2 = load(switchDoc, [{ name: secondMovie }], "https://example.com/two");
switchApi2.markPlayed(switched, 20);
assert.equal(switchApi2.current().url, secondMovie, "Open follows the media that is playing now");

const rapidFrameUrl = "https://rapidvid.net/vod/v1x7c5739a3";
const rapidFrame = el("iframe", { src: rapidFrameUrl, w: 960, h: 540, className: "video-player" });
const frameBody = el("body", {}, [rapidFrame]);
const frameDoc = {
  title: "Embedded movie",
  body: frameBody,
  documentElement: frameBody,
  querySelectorAll(sel) {
    if (sel === "iframe") return [rapidFrame];
    if (sel === "video" || sel === "video, *") return [];
    return [];
  }
};
const frameApi = load(frameDoc, [], "https://www.fullhdfilmizlesene.now/film/supergirl/");
assert.equal(frameApi.current().url, rapidFrameUrl, "an unresolved player iframe is sent to the desktop resolver");
assert.equal(frameApi.current().watchUrl, rapidFrameUrl);

const lazyRapidFrame = el("iframe", { src: "", "data-src": rapidFrameUrl, w: 960, h: 540, className: "video-player" });
const lazyFrameBody = el("body", {}, [lazyRapidFrame]);
const lazyFrameDoc = {
  title: "Lazy embedded movie",
  body: lazyFrameBody,
  documentElement: lazyFrameBody,
  querySelectorAll(sel) {
    if (sel === "iframe") return [lazyRapidFrame];
    if (sel === "video" || sel === "video, *") return [];
    return [];
  }
};
const lazyFrameApi = load(lazyFrameDoc, [], "https://www.fullhdfilmizlesene.now/film/supergirl/");
assert.equal(lazyFrameApi.current().url, rapidFrameUrl, "a visible lazy iframe is transferred before its src is activated");

console.log("sniff.test.mjs ok");
