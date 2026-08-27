import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";

const here = dirname(fileURLToPath(import.meta.url));
const langsPath = join(here, "..", "..", "extension", "content", "langs.js");
eval(readFileSync(langsPath, "utf8"));
const api = globalThis.GrokPlayerLangs;

const audioTracks = [
  { id: "und", j7: { id: "und", name: "Original", isDefault: true } },
  { id: "a", j7: { id: "ar.3", name: "Arabic" } },
  { id: "b", j7: { id: "bn.3", name: "Bangla" } },
  { id: "c", j7: { id: "zh-Hans", name: "Chinese (Simplified)" } },
  { id: "d", j7: { id: "zh-Hant", name: "Chinese (Traditional)" } },
  { id: "e", j7: { id: "ru", name: "Russian" } },
  { id: "f", j7: { id: "tr.3", name: "Turkish" } },
  { id: "g", j7: { id: "pt-BR", name: "Portuguese (Brazil)" } },
  { id: "h", xtags: "acont=dubbed:lang=hi" }
];

const captions = [
  { languageCode: "ar", languageName: "Arabic" },
  { languageCode: "bn", languageName: "Bangla" },
  { languageCode: "zh-Hans", languageName: "Chinese (Simplified)" },
  { languageCode: "zh-Hant", languageName: "Chinese (Traditional)" },
  { languageCode: "ru", languageName: "Russian" },
  { languageCode: "tr", languageName: "Turkish", isDefault: true },
  { languageCode: "en", languageName: "English (auto-generated)", kind: "asr" }
];

const translations = [
  { languageCode: "bn", languageName: "Bangla" },
  { languageCode: "ru", languageName: "Russian" },
  { languageCode: "zh-Hans", languageName: "Chinese (Simplified)" },
  { languageCode: "ja", languageName: "Japanese" },
  { languageCode: "ko", languageName: "Korean" },
  { languageCode: "hi", languageName: "Hindi" }
];

assert.equal(api.languageCode("tr.3"), "tr");
assert.equal(api.languageCode("bn.3"), "bn");
assert.equal(api.languageCode("zh-Hans"), "zh-Hans");
assert.equal(api.languageCode("zh-Hant"), "zh-Hant");
assert.equal(api.languageCode("pt-BR"), "pt-BR");
assert.equal(api.languageCode(".tr"), "tr");
assert.equal(api.languageCode("a.en"), "en");
assert.equal(api.languageCode("und"), "original");
assert.equal(api.languageCode("original"), "original");
assert.equal(api.languageCode("ru"), "ru");
assert.equal(api.languageCode("acont=dubbed:lang=bn"), "bn");
assert.equal(api.audioCode(audioTracks[6]), "tr");
assert.equal(api.audioCode(audioTracks[0]), "original");
assert.equal(api.audioCode(audioTracks[8]), "hi");
assert.equal(api.captionCode(captions[5]), "tr");
assert.equal(api.captionCode(captions[6]), "en:asr");
assert.equal(api.captionCode({ languageCode: "en", translationLanguage: { languageCode: "ru" } }), "ru");

const turkish = api.resolve(
  {
    getAudioTrack: audioTracks[6],
    getAvailableAudioTracks: audioTracks,
    captionTrack: captions[5],
    captionTracklist: captions,
    translationLanguages: translations,
    captionsOn: true
  },
  { audioPref: "auto", subPref: "auto" }
);
assert.equal(turkish.detected.audio, "tr");
assert.equal(turkish.detected.sub, "tr");
assert.equal(turkish.final.audio, "tr");
assert.equal(turkish.final.sub, "tr");
for (const code of ["bn", "ru", "zh-Hans", "zh-Hant", "pt-BR", "hi", "original", "tr", "ar"]) {
  assert.ok(turkish.available.audio.some((item) => item.code === code), "missing audio " + code);
}
for (const code of ["bn", "ru", "zh-Hans", "zh-Hant", "ja", "ko", "hi", "tr"]) {
  assert.ok(turkish.available.captions.some((item) => item.code === code), "missing caption " + code);
}

const bangla = api.resolve(
  {
    getAudioTrack: audioTracks[2],
    getAvailableAudioTracks: audioTracks,
    captionTrack: captions[1],
    captionTracklist: captions,
    captionsOn: true
  },
  { audioPref: "auto", subPref: "auto" }
);
assert.equal(bangla.final.audio, "bn");
assert.equal(bangla.final.sub, "bn");

const chinese = api.resolve(
  {
    getAudioTrack: audioTracks[3],
    getAvailableAudioTracks: audioTracks,
    captionTrack: captions[2],
    captionTracklist: captions,
    captionsOn: true
  },
  { audioPref: "auto", subPref: "auto" }
);
assert.equal(chinese.final.audio, "zh-Hans");
assert.equal(chinese.final.sub, "zh-Hans");

const forced = api.resolve(
  {
    getAudioTrack: audioTracks[6],
    getAvailableAudioTracks: audioTracks,
    captionTrack: captions[5],
    captionTracklist: captions,
    captionsOn: true
  },
  { audioPref: "ru", subPref: "zh-Hans" }
);
assert.equal(forced.detected.audio, "tr");
assert.equal(forced.detected.sub, "tr");
assert.equal(forced.final.audio, "ru");
assert.equal(forced.final.sub, "zh-Hans");

const capsOff = api.resolve(
  {
    getAudioTrack: audioTracks[6],
    getAvailableAudioTracks: audioTracks,
    captionTrack: captions[5],
    captionTracklist: captions,
    captionsOn: false
  },
  { audioPref: "auto", subPref: "auto" }
);
assert.equal(capsOff.final.sub, "", "Auto must not keep default tr when captions are off");

const staleTrack = api.resolve(
  {
    getAudioTrack: audioTracks[6],
    getAvailableAudioTracks: audioTracks,
    captionTrack: captions[5],
    captionTracklist: captions
  },
  { audioPref: "auto", subPref: "auto" }
);
assert.equal(staleTrack.final.sub, "", "stale getOption track is not a selection without the CC button");

const offPref = api.resolve(
  {
    getAudioTrack: audioTracks[6],
    getAvailableAudioTracks: audioTracks,
    captionTrack: captions[5],
    captionTracklist: captions,
    captionsOn: true
  },
  { audioPref: "original", subPref: "off" }
);
assert.equal(offPref.final.audio, "original");
assert.equal(offPref.final.sub, "off");

const noSticky = api.resolve(
  {
    getAudioTrack: audioTracks[0],
    getAvailableAudioTracks: audioTracks,
    captionTrack: null,
    captionTracklist: captions,
    captionsOn: false
  },
  { audioPref: "auto", subPref: "auto" }
);
assert.equal(noSticky.final.audio, "original");
assert.equal(noSticky.final.sub, "");

const fromAdaptive = api.resolve(
  {
    getAudioTrack: { audioTrack: { id: "ru.3", displayName: "Russian" } },
    playerAudioTracks: [
      { id: "bn.3", displayName: "Bangla" },
      { id: "ru.3", displayName: "Russian" },
      { id: "zh-Hans", displayName: "Chinese (Simplified)" }
    ],
    playerCaptionTracks: captions,
    playerTranslationLanguages: translations,
    captionsOn: true,
    captionTrack: { languageCode: "ru", languageName: "Russian" }
  },
  { audioPref: "auto", subPref: "auto" }
);
assert.equal(fromAdaptive.final.audio, "ru");
assert.equal(fromAdaptive.final.sub, "ru");
assert.ok(fromAdaptive.available.audio.some((item) => item.code === "bn"));
assert.ok(fromAdaptive.available.audio.some((item) => item.code === "zh-Hans"));

const fromTextTrack = api.resolve(
  {
    getAudioTrack: audioTracks[4],
    getAvailableAudioTracks: audioTracks,
    captionTrack: captions[5],
    captionTracklist: captions,
    captionsOn: true,
    textTracks: [{ language: "zh-Hant", label: "Chinese (Traditional)", mode: "showing" }]
  },
  { audioPref: "auto", subPref: "auto" }
);
assert.equal(fromTextTrack.final.audio, "zh-Hant");
assert.equal(fromTextTrack.detected.sub, "tr");

const menuMatch = api.matchTrack("Bangla", turkish.available.audio);
assert.equal(menuMatch, "bn");
assert.equal(api.matchTrack("Russian", turkish.available.captions), "ru");
assert.equal(api.matchTrack("Chinese (Simplified)", turkish.available.audio), "zh-Hans");
assert.equal(api.matchTrack("Off", turkish.available.captions), "off");

const anyLang = api.applyPref("ja", "tr", turkish.available.captions);
assert.equal(anyLang, "ja");

const matrix = [
  ["auto", "auto", "bn", "ru", "bn", "ru"],
  ["auto", "off", "bn", "ru", "bn", "off"],
  ["original", "auto", "bn", "ru", "original", "ru"],
  ["zh-Hans", "zh-Hant", "tr", "tr", "zh-Hans", "zh-Hant"],
  ["hi", "ja", "tr", "tr", "hi", "ja"]
];
for (const [audioPref, subPref, detAudio, detSub, wantAudio, wantSub] of matrix) {
  const result = api.resolve(
    {
      getAudioTrack: audioTracks.find((item) => api.audioCode(item) === detAudio),
      getAvailableAudioTracks: audioTracks,
      captionTrack: captions.find((item) => api.captionCode(item) === detSub),
      captionTracklist: captions,
      translationLanguages: translations,
      captionsOn: true
    },
    { audioPref, subPref }
  );
  assert.equal(result.final.audio, wantAudio, audioPref + "/" + subPref + " audio");
  assert.equal(result.final.sub, wantSub, audioPref + "/" + subPref + " sub");
}

const protoKo = "251;Cg8KBWFjb250EgZkdWJiZWQKCgoEbGFuZxICa28";
const protoEn = "251;ChEKBWFjb250EghvcmlnaW5hbAoKCgRsYW5nEgJlbg";
const protoZh = "251;Cg8KBWFjb250EgZkdWJiZWQKDwoEbGFuZxIHemgtSGFucw";
assert.equal(api.languageCode(protoKo), "ko", "protobuf audio id is Korean");
assert.equal(api.languageCode(protoEn), "en", "protobuf audio id is English");
assert.equal(api.languageCode(protoZh), "zh-Hans", "protobuf audio id is Chinese");
assert.equal(api.audioCode({ id: protoKo }), "ko");
assert.equal(api.audioCode(protoKo), "ko");

const noJ7 = [
  { id: protoEn, displayName: "English original" },
  { id: protoKo, displayName: "Korean" },
  { id: "251;Cg8KBWFjb250EgZkdWJiZWQKCgoEbGFuZxICdHI", displayName: "Turkish" }
];
const currentProto = api.resolve(
  {
    getAudioTrack: { id: protoKo },
    getAvailableAudioTracks: noJ7,
    captionsOn: true,
    captionTrack: { languageCode: "en", languageName: "English" }
  },
  { audioPref: "auto", subPref: "auto" }
);
assert.equal(currentProto.detected.audio, "ko", "current proto id without j7 is Korean");
assert.equal(currentProto.final.audio, "ko");
assert.ok(currentProto.available.audio.some((item) => item.code === "ko"));

const stringCurrent = api.resolve(
  {
    getAudioTrack: protoKo,
    getAvailableAudioTracks: noJ7
  },
  { audioPref: "auto", subPref: "auto" }
);
assert.equal(stringCurrent.detected.audio, "ko", "string getAudioTrack proto id");

const namedCurrent = api.resolve(
  {
    getAudioTrack: { name: "Korean" },
    getAvailableAudioTracks: noJ7
  },
  { audioPref: "auto", subPref: "auto" }
);
assert.equal(namedCurrent.detected.audio, "ko", "name-only getAudioTrack");

const zyKey = api.resolve(
  {
    getAudioTrack: { id: protoKo, zy: { id: "ko.3", name: "Korean" } },
    getAvailableAudioTracks: [
      { id: protoEn, zy: { id: "en.4", name: "English original" } },
      { id: protoKo, zy: { id: "ko.3", name: "Korean" } }
    ]
  },
  { audioPref: "auto", subPref: "auto" }
);
assert.equal(zyKey.detected.audio, "ko", "YouTube zy field after j7 rename");
assert.equal(zyKey.available.audio.find((item) => item.code === "ko").name, "Korean");

const renamedKey = api.resolve(
  {
    getAudioTrack: { k8: { id: "ko.3", name: "Korean" } },
    getAvailableAudioTracks: [
      { k8: { id: "en.4", name: "English original" } },
      { k8: { id: "ko.3", name: "Korean" } }
    ]
  },
  { audioPref: "auto", subPref: "auto" }
);
assert.equal(renamedKey.detected.audio, "ko", "nested renamed YouTube key");

const htmlSelected = api.resolve(
  {
    getAudioTrack: {},
    getAvailableAudioTracks: noJ7,
    htmlAudioTracks: [
      { language: "en", label: "English original", enabled: false },
      { language: "ko", label: "Korean", enabled: true }
    ]
  },
  { audioPref: "auto", subPref: "auto" }
);
assert.equal(htmlSelected.detected.audio, "ko", "html audioTracks enabled");

const idMatch = api.resolve(
  {
    getAudioTrack: { id: protoKo },
    getAvailableAudioTracks: [
      { id: protoEn, j7: { id: "en.4", name: "English original" } },
      { id: protoKo, j7: { id: "ko.3", name: "Korean" } }
    ]
  },
  { audioPref: "auto", subPref: "auto" }
);
assert.equal(idMatch.detected.audio, "ko", "match listed track by proto id");
assert.ok(idMatch.available.audio.find((item) => item.code === "ko").selected);

console.log("langs.test.mjs passed");
