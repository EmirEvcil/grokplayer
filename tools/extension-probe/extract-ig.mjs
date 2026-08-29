import { readFileSync, writeFileSync } from "node:fs";

const t = readFileSync((process.env.TEMP || "/tmp") + "/ig.html", "utf8");
const urls = [...t.matchAll(/https:\\?\/\\?\/[^"'\\\s<>]+/g)].map((m) => m[0].replace(/\\\//g, "/"));
const vid = urls.filter((u) => /t16|t2\/|o1\/v|\.mp4|video_dash|video_versions|playable/i.test(u) && !/\.js(\?|$)/.test(u));
writeFileSync((process.env.TEMP || "/tmp") + "/ig-urls.txt", [...new Set(vid)].slice(0, 80).join("\n"));
console.log("unique", new Set(vid).size);
console.log("sample", [...new Set(vid)].slice(0, 15).join("\n"));
const og = t.match(/og:video[^>]{0,180}/g);
console.log("og", og && og.slice(0, 6));
const vv = t.match(/video_versions.{0,500}/);
console.log("vv", vv && vv[0].slice(0, 500));
const short = t.match(/Da5Y8qhsLcU.{0,200}/);
console.log("code", short && short[0].slice(0, 200));
