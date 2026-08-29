import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";
import vm from "node:vm";

const here = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(join(here, "..", "..", "extension", "content", "chip.js"), "utf8");
const openChip = readFileSync(join(here, "..", "..", "extension", "content", "open-chip.js"), "utf8");
const youtube = readFileSync(join(here, "..", "..", "extension", "content", "youtube.js"), "utf8");
const background = readFileSync(join(here, "..", "..", "extension", "background.js"), "utf8");
const css = readFileSync(join(here, "..", "..", "extension", "content", "youtube.css"), "utf8");
const popupHtml = readFileSync(join(here, "..", "..", "extension", "popup", "popup.html"), "utf8");

assert.doesNotMatch(openChip, /sourcePick|source-menu|class="pick"/);
assert.match(openChip, /open-active/);
assert.doesNotMatch(openChip, /HOLD_MS|3000/);
assert.match(openChip, /open-url/);
assert.match(openChip, /chipGone/);
assert.match(openChip, /Skip ad/);
assert.match(youtube, /url: watchUrl|url: info\.watchUrl/);
assert.match(youtube, /if \(!info\) \{\s*return;/s);
assert.match(background, /function usesPageCatalog/);
assert.match(background, /function catalogUrl/);
assert.match(background, /params\.set\("sound"/);
assert.doesNotMatch(background, /url: page\.split/);
assert.match(background, /dailymotion/);
assert.match(background, /twitch\\.tv/);
assert.match(background, /pageKindFromUrl\(url\) === "live"/);
assert.match(background, /transferable\(incoming\)/);
assert.doesNotMatch(youtube, /sourcePick|source-menu|class="pick"/);
assert.doesNotMatch(css, /source-menu|\.pick\b/);
assert.doesNotMatch(popupHtml, /sourcePick|source-menu/);
assert.match(source, /Open in GrokPlayer/);
assert.match(source, /attachShadow/);
assert.match(source, /viewBox="0 0 12 12"/);
assert.match(source, /class="skip"/);
assert.doesNotMatch(source, /class="pick"|▾|&#9662;/);

const removed = [];
const host = {
  isConnected: true,
  style: {
    setProperty(name, value) {
      this[name] = value;
    }
  },
  setAttribute() {},
  attachShadow() {
    return {
      innerHTML: "",
      querySelector(sel) {
        return {
          addEventListener() {},
          textContent: "",
          classList: { toggle() {} },
          sel
        };
      }
    };
  },
  remove() {
    removed.push("host");
  }
};

const context = {
  window: { innerWidth: 1280, innerHeight: 800, addEventListener() {} },
  document: {
    documentElement: {
      appendChild(node) {
        this.child = node;
      }
    },
    body: {},
    querySelectorAll() {
      return [];
    },
    createElement() {
      return host;
    }
  },
  chrome: { runtime: { getURL: () => "icon.png" } }
};
vm.createContext(context);
vm.runInContext(source, context);

const ui = context.window.GrokPlayerChipUi;
assert.ok(ui);
const chip = ui.show({ kind: "vod", target: { getBoundingClientRect: () => ({ width: 800, height: 450, top: 40, right: 900, bottom: 490, left: 100 }) } });
assert.equal(chip, host);
assert.equal(host.style.display, "inline-flex");
assert.ok(!String(ui.css).includes("pick"));
ui.hide();
assert.deepEqual(removed, ["host"]);

console.log("chip.test.mjs ok");
