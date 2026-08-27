import { readFileSync } from "node:fs";

const raw = readFileSync(new URL("./last-dump.json", import.meta.url), "utf8");
const doc = JSON.parse(raw);
const blob = doc.isolated?.result?.value || doc.page;
const page = typeof blob === "string" ? JSON.parse(blob) : blob;
const dump = page?.pageDump;
if (!dump) {
  console.log("no pageDump", Object.keys(doc), doc.isolated?.result);
  process.exit(1);
}

function slim(track) {
  if (track == null || typeof track !== "object") {
    return track;
  }
  if (track.error) {
    return track;
  }
  return {
    keys: track.keys,
    id: track.id,
    kind: track.kind,
    lang: track.lang,
    language: track.language,
    languageCode: track.languageCode,
    displayName: track.displayName,
    name: track.name,
    xtags: track.xtags,
    isSelected: track.isSelected,
    selected: track.selected,
    isDefault: track.isDefault,
    audioIsDefault: track.audioIsDefault,
    vssId: track.vssId,
    translationLanguage: track.translationLanguage
  };
}

console.log(JSON.stringify({
  href: page.href,
  chip: page.chip,
  player: page.player,
  getAudioTrack: slim(dump.getAudioTrack),
  tracks: Array.isArray(dump.getAvailableAudioTracks) ? dump.getAvailableAudioTracks.map(slim) : dump.getAvailableAudioTracks,
  captionTrack: slim(dump.captionTrack),
  captionTracklist: Array.isArray(dump.captionTracklist) ? dump.captionTracklist.map(slim) : dump.captionTracklist,
  menu: dump.menuItems,
  height: dump.videoHeight,
  quality: dump.playbackQuality,
  levels: dump.qualityLevels,
  textTracks: dump.textTracks,
  button: dump.subtitleButton,
  usefulFns: (dump.playerFns || []).filter((name) => /audio|caption|sub|option|quality|track/i.test(name))
}, null, 2));
