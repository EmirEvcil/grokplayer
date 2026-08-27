# Building Grok Player

A learning guide and implementation plan for a Windows / NVIDIA video player and a later IPTV app, built as a **WinUI 3 shell around libmpv**.

We do not write a decoder, a clock, or a seek implementation. mpv already is that machine. We write the product: windows, library, PiP, resume, settings, streaming UI, recording, and the IPTV shell that embeds the same player host.

---

## How to read this document

You have not built a player before. You do not need to become an FFmpeg pipeline engineer to ship a capable one. You *do* need to understand what mpv is doing for you, how to drive it, and where embedding goes wrong.

| Section | What it is for |
|---|---|
| [0. Decisions](#0-decisions-we-already-locked) | What we will not reopen every week |
| [1. Why libmpv](#1-why-libmpv) | What we gave up and what we gained |
| [2. What a player still is](#2-what-a-player-still-is) | Enough media literacy to debug and configure |
| [3. How you talk to libmpv](#3-how-you-talk-to-libmpv) | Commands, properties, events |
| [4. Embedding on Windows](#4-embedding-on-windows) | `wid` vs render API, WinUI, HWND |
| [5. NVIDIA, HDR, audio](#5-nvidia-hdr-audio) | The mpv options that matter on your machine |
| [6. Architecture](#6-architecture) | Wrapper, shells, IPTV, Android |
| [7. What you must pay attention to](#7-what-you-must-pay-attention-to) | The landmines |
| [8. What we will not do](#8-what-we-will-not-do) | Scope discipline |
| [9. Build plan](#9-build-plan) | Phases and definitions of done |
| [10. Learning path](#10-learning-path) | What to study, in order |
| [11. Glossary](#11-glossary) | Quick lookup |

---

## 0. Decisions we already locked

| Decision | Choice | Why |
|---|---|---|
| Playback engine | **libmpv** (official C API) | Clock, seek, hwdec, HDR, subs, HLS/DASH already exist and are maintained |
| First OS | Windows 10/11 desktop | Your machine, DXGI HDR, Compact Overlay PiP |
| GPU priority | NVIDIA via mpv `d3d11` + `d3d11va` | Native Windows path. We do not build AMF/VAAPI/VideoToolbox |
| Other desktops | Not now | No macOS / Linux app |
| Future ports | Android phone + Google TV | `mpv-android`-style libmpv + a Kotlin shell |
| Next product | IPTV app embeds the same host | One C#/C++ player host, two WinUI apps |
| Windows GUI | **WinUI 3 + Windows App SDK + C# (.NET 8/9)** | Native Windows 11 product UI |
| First slice | Local files in a real window | Open, play, pause, seek, volume, HW decode. Then library/PiP/network |
| Custom FFmpeg core | **No** | We pivoted. The old plan is obsolete. |

“All formats” now means: whatever the libmpv/FFmpeg build we ship can open. That is already most containers and codecs people actually play. DRM (Netflix, Disney+, Widevine, PlayReady) is still out. libmpv cannot do that either.

---

## 1. Why libmpv

The first plan was: write our own FFmpeg pipeline (demux → decode → queues → clock → D3D11/WASAPI). That is a real project. It is also the project mpv has been for more than a decade.

You wanted:

- a highly capable player
- H.264 / HEVC / AV1 / AAC
- hardware decode, HDR, PiP
- offline resume
- online streams + record/download
- a later IPTV app that *embeds* the engine
- Android / Google TV later
- no player-development experience

Those goals argue for **owning the app**, not reinventing the clock. A homemade pipeline that seeks cleanly, stays in sync on VFR, maps HDR10 through DXGI, renders ASS subtitles, and plays HLS without buffer deadlocks is years of work. libmpv already does it. IINA, smplayer, mpv.net, several IPTV frontends, and mpv-android are all “a GUI around libmpv.” That is a respected architecture, not a shortcut that makes a toy.

### What we still own

The product. Specifically:

- WinUI chrome, library, playlists, settings
- Picture-in-picture, SMTC, file associations
- Resume and watch history
- Our NVIDIA/HDR defaults (a shipped config, not the user’s `%APPDATA%\mpv`)
- Stream recording / download UX
- The IPTV app (M3U, EPG, channels) talking to the same host
- How video sits inside *our* window

### What we do not own

- A/V sync
- Keyframe seek
- NVDEC/D3D11VA interop
- Subtitle rendering (including ASS)
- HLS/DASH client
- Tone mapping

We *configure* those. We do not implement them.

### What you still learn

You still need to know what a container, a codec, PTS, a keyframe, hwdec, and a swap chain are. Otherwise you cannot set options, read `hwdec-current`, or tell a network buffer problem from a GUI bug. You do not need to write `avcodec_send_packet` loops.

---

## 2. What a player still is

Keep this picture. libmpv *is* this machine. Your app sits on the right.

```
file/URL → demux → decode → queues → CLOCK → present
                                           ├── D3D11 (picture)
                                           └── WASAPI (sound)
                ↑
         your app (commands / properties / a window)
```

### Vocabulary you actually need

| Term | Meaning | Why you care |
|---|---|---|
| Container | The box: MKV, MP4, WebM, TS, HLS | Never trust the file extension. Probe / ask mpv `file-format` |
| Codec | How a stream is compressed: H.264, HEVC, AV1, AAC, Opus | Shows up in the HUD and in “why did hwdec fail?” |
| Packet vs frame | Compressed vs decoded | You will not touch these. mpv does. |
| PTS | Presentation timestamp | Seek and the timeline are in seconds of PTS |
| Keyframe | A frame you can start decoding from | Seek lands on or after one unless you use exact/hr-seek |
| Track | One video, audio, or subtitle stream | `sid`, `aid`, `vid` properties |
| hwdec | Hardware decode | `d3d11va` on your NVIDIA box |
| VO | Video output driver | We want `gpu-next` |
| OSD / OSC | On-screen text / mpv’s built-in control bar | We turn OSC off and draw WinUI controls |

### Commands you should become fluent with *as a user* first

Before any C#, install a normal mpv build (shinchiro Windows build is fine) and play files you actually watch:

```
mpv --hwdec=d3d11va --vo=gpu-next --gpu-api=d3d11 "D:\videos\sample.mkv"
```

Then press `i` (stats), seek, pause, cycle audio (`#`) and subs (`j`). That is the engine your app will be driving. If standalone mpv cannot play a file well, our app will not either.

`ffprobe` is still useful to *see* what a file contains:

```
ffprobe -hide_banner -show_streams "D:\videos\sample.mkv"
```

---

## 3. How you talk to libmpv

libmpv is a C library (`client.h`). It is not “run the mpv.exe process and parse stdout” (that exists as JSON IPC; we do not use it for the embedded player). Everything goes through one handle: `mpv_handle*`.

### Life cycle

```
mpv_create()
  → set options that must exist before init
       (wid, vo, hwdec, config, config-dir, osc, input-*)
mpv_initialize()
  → loadfile / seek / pause / set properties
  → observe properties, wait for events
mpv_terminate_destroy()
```

Create **one handle per playback surface** (one for the main window). Picture-in-picture is the *same* handle in a smaller window, or a second handle only if you truly need two videos at once. IPTV channel-zap is `loadfile` on the same handle, not a new process.

### The three verbs

Almost the entire app is these three:

**1. Commands** — “do this”

```c
const char *cmd[] = { "loadfile", path, "replace", NULL };
mpv_command(mpv, cmd);

const char *cmd[] = { "seek", "30", "relative", NULL };
mpv_command(mpv, cmd);
```

Important commands we will use:

| Command | Use |
|---|---|
| `loadfile <path> replace` | Open a file or URL |
| `loadfile <path> append` | Queue next |
| `stop` / `playlist-play-index` | Stop / jump playlist |
| `seek <sec> relative\|absolute\|absolute-percent` | Timeline |
| `cycle pause` or set `pause` | Play / pause |
| `quit` | Tear down (usually we just `stop` + destroy) |
| `screenshot` | Still frame |
| `sub-add` / `audio-add` | External tracks |
| `script-message` | Talk to optional Lua later |

Prefer **async** variants (`mpv_command_async`) from the UI thread so a slow `loadfile` cannot freeze WinUI.

**2. Properties** — “read or write this knob / this state”

```c
mpv_set_property_string(mpv, "pause", "yes");
double t = 0;
mpv_get_property(mpv, "time-pos", MPV_FORMAT_DOUBLE, &t);
```

Properties we will live in:

| Property | Meaning |
|---|---|
| `time-pos` | Current time, seconds |
| `duration` | Length, or unavailable |
| `pause` | yes/no |
| `eof-reached` | End of file |
| `path` / `media-title` / `file-format` | What is open |
| `video-params` / `audio-params` | Resolution, pixfmt, sample rate, … |
| `track-list` | All streams (JSON-ish node) |
| `aid` / `sid` / `vid` | Selected tracks |
| `volume` / `mute` | 0–100, bool |
| `hwdec` / `hwdec-current` | Requested vs actually active |
| `paused-for-cache` / `cache-buffering-state` | Network spinner |
| `estimated-vf-fps` / `drop-frame-count` | HUD |
| `video-unscaled` / `keepaspect` / `panscan` | Picture fit |
| `ontop`, `fullscreen` | Window hints (we mostly do these in WinUI) |

`mpv_observe_property` is how the seek bar moves: you do **not** poll `time-pos` on a 16 ms UI timer if you can avoid it. Observe, get an event, dispatch to the UI thread, update the slider.

**3. Events** — “something happened”

A background loop (or the wakeup callback) calls `mpv_wait_event`. You care about:

| Event | Typical reaction |
|---|---|
| `MPV_EVENT_PROPERTY_CHANGE` | Update seek bar, pause icon, title |
| `MPV_EVENT_FILE_LOADED` | Read duration, tracks, start resume logic |
| `MPV_EVENT_END_FILE` | Next playlist item, or idle |
| `MPV_EVENT_LOG_MESSAGE` | Our log window (enable `mpv_request_log_messages`) |
| `MPV_EVENT_SHUTDOWN` | Handle is dead |

### Options vs properties

Before `mpv_initialize`, some things must be **options** (`mpv_set_option_string`): `wid`, `vo`, `gpu-api`, `hwdec`, `config`, `config-dir`, `osc`, `input-default-bindings`.

After init, prefer **properties** (`mpv_set_property_string`). The distinction is a libmpv quirk. If something “does nothing,” you probably set it at the wrong time.

---

## 4. Embedding on Windows

This is the only hard part of the *player* side. The rest is a WinUI app.

libmpv will not hand you an RGBA bitmap every frame. That would kill 4K. You give it a place to draw.

### Method A — native window (`wid`)  ← start here

You create an HWND. You set the `wid` option to that handle (as an integer string). libmpv creates its video child (or draws into that window) and fills it, letterboxing if the aspect ratios differ.

This is what the official C# example, Mpv.NET, and mpv.net do. On Win32 it is the path that keeps `vo=gpu-next` + `gpu-api=d3d11` + `hwdec=d3d11va` + HDR simplest.

**WinUI 3:** host a child HWND inside the XAML tree (`HWND` interop / a dedicated “video host” control), pass that handle as `wid`, and put transport controls in XAML *around* it (and as overlays on a transparent grid if we accept the usual HWND-vs-XAML z-order fights).

Pros: simplest, best NVIDIA/D3D11/HDR path, mpv owns present.  
Cons: XAML glass/Acrylic cannot composite *through* the video; overlays need care; resize must be forwarded.

### Method B — render API (`render.h`)

You own an OpenGL (or ANGLE) context. On each vsync you call `mpv_render_context_render()`. Overlays become trivial because the video is just another GL/D3D surface in *your* composition.

Pros: best for fancy on-video XAML/GL HUD.  
Cons: more code, you now own a GL/ANGLE device; D3D11-native render-API support is historically weaker than GL; easy to get vsync and context-current wrong.

**Decision:** Phase 1 uses **`wid` + HWND host**. If overlays or SwapChainPanel become painful, we revisit the render API. We do not start there.

### What the WinUI window actually contains

```
┌─────────────────────────────────────────────┐
│  WinUI 3 chrome  (title, nav, library)      │
│  ┌───────────────────────────────────────┐  │
│  │  HWND video host  (libmpv wid)        │  │
│  │                                       │  │
│  └───────────────────────────────────────┘  │
│  transport bar, time, volume  (XAML)        │
└─────────────────────────────────────────────┘
```

Fullscreen: grow the HWND to the monitor (or a borderless WinUI window) and hide chrome.  
PiP: `AppWindowPresenterKind.CompactOverlay` on the **same** window/handle, or a tiny second WinUI window that *reparents* the same HWND. Do not start a second `mpv_handle` for PiP.

### Do not use

- `MediaPlayerElement` (Media Foundation, not mpv)
- Launching `mpv.exe` hidden and hoping
- Electron / WebView `<video>`

---

## 5. NVIDIA, HDR, audio

We ship **our** defaults. We do not read the user’s `~\AppData\Roaming\mpv\mpv.conf` unless they opt in. Otherwise a random `vo=x` in their home directory will make our app “broken on my PC.”

### Baseline Windows / NVIDIA profile

Set these (or write them into an isolated `config-dir` we own):

```
vo=gpu-next
gpu-api=d3d11
hwdec=d3d11va
hwdec-codecs=h264,hevc,av1,vp9,av01
profile=gpu-hq          # optional; can wait
ao=wasapi
osc=no
input-default-bindings=no
input-vo-keyboard=no
keep-open=yes
keep-open-pause=yes
```

`hwdec=d3d11va` on NVIDIA **is** NVDEC. `hwdec=nvdec` is an alternate FFmpeg path; start with d3d11va (same silicon, better interop with the D3D11 VO). If a file fails HW, mpv should fall back — confirm with `hwdec-current` and `hwdec-interop`.

`d3d11-adapter=NVIDIA` only if a machine has two GPUs and picks the Intel panel GPU. Do not set it blindly.

### HDR

mpv + `gpu-next` + `gpu-api=d3d11` is one of the better HDR players on Windows.

- Windows HDR on, HDR10/HLG file: mpv can output HDR to DXGI.
- Windows HDR off: mpv tone-maps to SDR (`target-trc`, `tone-mapping`, `hdr-compute-peak`).
- We do not invent a tone mapper. We pick defaults and expose 2–3 settings later.

Verify against standalone mpv and, if needed, against another known-good player. If standalone mpv looks wrong, it is a config problem, not a WinUI problem.

### Audio

`ao=wasapi` shared is the default. Exclusive mode is an advanced setting (bit-perfect, breaks other apps). Volume is the `volume` property (0–100). Do not also duck in WASAPI unless we have a reason.

### Recording / download

mpv can remux while playing:

- `stream-record=<path>` property — writes the incoming stream to disk
- Useful for IPTV and HTTP/HLS

Limits: not a perfect download manager (retries, multi-connection, pick-a-quality UI). For a serious downloader we may add a **separate** FFmpeg/yt-dlp-style job later. Do not screen-capture. Do not start that job in phase 1.

---

## 6. Architecture

```
┌──────────────────────────────────────────────┐
│  grok-player.exe          grok-iptv.exe      │
│  WinUI 3 / C#             WinUI 3 / C#       │
│         \                     /              │
│          \                   /               │
│           PlayerHost  (C# thin wrapper)      │
│           P/Invoke → libmpv-2.dll            │
│           vo=gpu-next · d3d11va · wasapi     │
└──────────────────────────────────────────────┘
                    │
                    │  same idea, JNI + libmpv
                    ▼
           Android / Google TV shell
```

### Repo layout (target)

```
grok-player/
  docs/
    BUILDING-THE-PLAYER.md
  src/
    Grok.Player.Mpv/          C# libmpv wrapper (P/Invoke + PlayerHost)
    Grok.Player.App/          WinUI 3 desktop player
    Grok.Player.Iptv/         later
  native/
    libmpv/                   shipped libmpv-2.dll + codecs/deps + headers
  tools/
    README-mpv-build.txt      where the DLL came from, exact version
```

There is **no** `engine/` C++ player. A tiny C++ helper is allowed later only if WinUI HWND interop is ugly in C#. The wrapper is C# first.

### PlayerHost — the only mpv-facing type

Both apps talk to this, never to raw P/Invoke from a page.

```
Create(hwnd)
Destroy()
Open(pathOrUrl)
Play() / Pause() / Stop()
Seek(TimeSpan, SeekOrigin)
SetVolume(double)
SelectAudio(id) / SelectSub(id)
StartRecord(path) / StopRecord()
Events: TimeChanged, Loaded, Ended, Buffering, Error, TracksChanged
Properties: Position, Duration, IsPaused, MediaTitle, HwdecCurrent
```

This is our “engine API.” It just happens to be implemented by libmpv instead of homemade queues.

### Isolate mpv from the user’s mpv

On create, before `mpv_initialize`:

```
config=no                  # or config-dir=<our AppData\GrokPlayer\mpv>
osc=no
input-default-bindings=no
input-vo-keyboard=no
```

If we want user-overridable extra options, we load **our** `extras.conf` from our AppData, never `%APPDATA%\mpv` by default.

### IPTV later

The IPTV app is another WinUI project that:

- parses M3U / M3U8 playlists
- shows an EPG
- calls `PlayerHost.Open(streamUrl)`
- uses `paused-for-cache` for a spinner
- uses `stream-record` for “record this programme”

It does not contain a second player stack.

### Android / Google TV later

Different GUI (Kotlin / Compose / Leanback). Same ideas: one libmpv handle, same commands (`loadfile`, `seek`, `pause`), a `Surface` instead of an HWND. The C# wrapper does not port; the *command vocabulary* does. That is enough.

---

## 7. What you must pay attention to

Homemade cores die on clocks. libmpv apps die on **lifecycle, config isolation, threading, and the video HWND**.

### Lifecycle

1. **`wid` before `mpv_initialize`.** Setting it later is too late or racy.
2. **Destroy order.** Stop playback → detach `wid` (set to `0`) → destroy the handle → destroy the HWND. Reverse of that = access violation on close. This is the #1 crash in libmpv frontends.
3. **Resize.** When the host HWND size changes, mpv notices a parent resize; if it does not, force a `vo` resize / set `window-scale` carefully. Do not leave a 1×1 swap chain.
4. **One UI thread rule for HWND.** Create and destroy the video HWND on the thread that owns it (the WinUI thread).
5. **Do not `mpv_terminate_destroy` from inside an mpv event callback.** Deadlock. Marshal “please tear down” to your own thread.

### Config and input

6. **Isolate config.** A user’s personal `mpv.conf` will change `vo`/`hwdec` and you will spend days “debugging our app.”
7. **Turn off OSC and default keybindings.** Otherwise mpv and WinUI both handle Space, and you get double-toggle pause or a built-in bar we do not want.
8. **`keep-open=yes`.** Without it, EOF destroys the last frame and sometimes the VO. The shell wants “paused on last frame” so we can Next or replay.

### Threading

9. **Commands from the UI thread must be async** (`mpv_command_async`) or run on a worker. `loadfile` of a slow/network path will freeze the window if you call it synchronously.
10. **Events → UI dispatcher.** The event loop is not the XAML thread. Marshal every property change before touching a control.
11. **Do not poll `time-pos` at 60 Hz via `mpv_get_property` on the UI thread** if observe exists. Observe + throttle the slider to 4–10 Hz.

### Embedding / picture

12. **DPI.** HWND size is in physical pixels. WinUI `ActualWidth` is DIP. Convert with `XamlRoot.RasterizationScale` or the window DPI. Wrong = black bars or a clipped picture.
13. **HWND z-order.** XAML overlays on top of a child HWND are a known pain. Transport bar *below* the video is easy; fade-over-video is the part that may push us to the render API later.
14. **Do not use a second `mpv_handle` for PiP.**
15. **Letterboxing.** `wid` mode letterboxes inside the HWND. Size the host to the area you want; do not also add our own bars unless we are doing a deliberate UI.

### Playback correctness (now a config problem)

16. **Trust standalone mpv first.** If mpv.exe plays it and we do not, our options/`wid`/lifecycle are wrong.
17. **Read `hwdec-current`.** If it is `no` on a 4K HEVC file, we are in software and the GPU is idle. Fix options, do not rewrite a decoder.
18. **Unknown duration.** Live IPTV and some files have no `duration`. The seek bar must disable, not divide by zero.
19. **UTF-8 paths.** libmpv wants UTF-8. C# `string` → UTF-8 bytes. Do not pass ANSI.
20. **Network on the UI thread.** Same as (9). Show `paused-for-cache` instead of hanging.

### Shipping

21. **Ship the exact libmpv build** (shinchiro or self-built) next to the exe, including its DLL dependencies. A missing `libmpv-2.dll` is a silent start crash.
22. **Pin the version.** Note it in `tools/README-mpv-build.txt`. Upgrading libmpv is a deliberate step; GPU/VO defaults change.
23. **LGPL.** libmpv/FFmpeg are typically LGPL when dynamically linked. Keep them as DLLs. Do not static-link if you want to stay LGPL-clean.
24. **HEVC patents** still exist if you distribute a commercial installer. Personal use on your PC is a different question.

### Product

25. **Do not build the library/IPTV UI before a file plays in the HWND.** Same rule as before, just a shorter road.
26. **Recording is `stream-record` or a side FFmpeg job**, never screen capture.
27. **Our wrapper is the ABI.** Pages do not call `mpv_command` directly, or the IPTV app will fork a second dialect.

---

## 8. What we will not do

- Write our own demux/decode/clock/present pipeline
- Use libvlc / Media Foundation / `MediaPlayerElement` / `<video>`
- Use mpv JSON IPC to a hidden `mpv.exe` as the primary embed (worse lifecycle, worse PiP)
- Depend long-term on an unmaintained NuGet wrapper if we can P/Invoke `client.h` in an afternoon
- Start with the OpenGL render API
- Start with IPTV, download manager, or a media library
- Read the user’s global `mpv.conf` by default
- Promise DRM
- Promise “every format, no limits” beyond what shipped libmpv can open
- Couple the Windows GUI to MAUI “so Android is free”

---

## 9. Build plan

Each phase has a definition of done. This list is much shorter than a custom-core plan, because the engine already exists. The risk moved to **embedding and product**.

### Phase 0 — Workshop

- WinUI 3 / Windows App SDK app that starts
- Vendor a known libmpv Windows build (`libmpv-2.dll` + deps) into `native/libmpv`
- Record the exact version and URL in `tools/README-mpv-build.txt`
- P/Invoke surface: `mpv_create`, `initialize`, `command`, `set/get_property_string`, `wait_event`, `terminate_destroy`

**Done when:** the app starts, loads the DLL, creates and destroys a handle, logs `mpv_client_api_version()`.

### Phase 1 — A window that plays a file

- HWND video host inside the WinUI window
- `wid` + baseline NVIDIA options
- Open file dialog → `loadfile`
- Play / pause / seek bar / time labels / volume
- Event loop on a worker; UI updates on the dispatcher
- Clean shutdown (the destroy-order test)

**Done when:**

- An H.264+AAC file plays in the window, in sync, with working seek and pause
- `hwdec-current` is `d3d11va` on your NVIDIA GPU
- Close while playing, 20 times: no crash
- A path with non-ASCII characters works
- Resize the window: video follows, no frozen swap chain

This phase *is* the player. Do not skip the crash-on-close test.

### Phase 2 — Tracks, HUD, resume

- Audio / subtitle cycle from our UI (`aid` / `sid`, `track-list`)
- Optional external `.srt`
- OSD-less; our own title + time
- Show codec, resolution, `hwdec-current` in a stats flyout (like mpv `i`)
- Resume: our DB maps file path/hash → `time-pos` on close, seek on next open
- `keep-open` so EOF does not black the window

**Done when:** you can live on this window for local files. Reopening a movie offers to continue.

### Phase 3 — Windows integration

- Keyboard shortcuts (Space, arrows, F, M) handled by *us*, not mpv
- SMTC (headset, keyboard media keys, Win+A OSD)
- File associations / “open with”
- Fullscreen (borderless, hide chrome)
- Compact Overlay PiP, same handle
- Remember window placement

**Done when:** PiP does not create a second decoder (one handle). Media keys pause our player, not Spotify by accident when we are focused.

### Phase 4 — Library shell

- Folder scan / watch folder
- Grid/list, posters later if you want them
- Playlists in the *app*, pushed to mpv via `loadfile append` / `playlist-*`
- Settings page that writes *our* isolated config (hwdec on/off, volume default, theme)

**Done when:** the app feels like a Windows 11 product, not a file dialog + HWND.

### Phase 5 — Network streams

- `loadfile` on HTTP(S) and HLS
- Buffering UI from `paused-for-cache` / `cache-buffering-state`
- Timeouts and a real error string (`MPV_EVENT_END_FILE` reason)
- Isolated cache directory under our AppData

**Done when:** a public HLS test stream plays; pulling the cable shows an error, not a wedged UI.

### Phase 6 — Record / download

- `stream-record` for “save what I am watching”
- Job list in the UI
- Later, if needed: a side FFmpeg process for “download this VOD at this quality”

**Done when:** a local file and an HLS VOD can be saved and the result plays in our app and in standalone mpv.

### Phase 7 — HDR polish + NVIDIA extras

- Confirm HDR10 on a Windows-HDR display vs standalone mpv
- Expose tone-map / peak settings
- Optional: `d3d11-adapter`, RTX VSR if the mpv build’s `d3d11vpp` filter supports it

**Done when:** an HDR10 sample matches standalone mpv. No custom shader work required.

### Phase 8 — IPTV app

- New WinUI project, same `Grok.Player.Mpv`
- M3U playlist import
- Channel list + now-playing
- EPG if you have XMLTV
- Zapping = `loadfile replace` on the same handle (measure how fast)
- Record from phase 6

**Done when:** a real playlist you use daily is watchable, and the desktop player still works unchanged.

### Phase 9 — Android / Google TV (much later)

- Separate repo or `src/android`
- libmpv + Surface
- Same command vocabulary, new UI

### What “start” means tomorrow

**Phase 0:** empty WinUI app + libmpv DLL loads + API version logged.

Then phase 1 until a file plays and close-while-playing does not crash. That is the whole game for a while.

---

## 10. Learning path

Study and building stay in lockstep. Do not read the entire mpv manual.

### Before phase 0

1. Install standalone mpv. Play five files you actually watch with  
   `--hwdec=d3d11va --vo=gpu-next --gpu-api=d3d11`
2. Press `i` and read the stats page. Find hwdec, codec, fps.
3. `ffprobe` those same files so container vs codec is not abstract.

### During phase 0–1

4. Read **`client.h` comments** (the real libmpv doc). Not a random blog.
5. Official embed notes: [mpv-examples/libmpv](https://github.com/mpv-player/mpv-examples/blob/master/libmpv/README.md) — especially `wid` vs render API.
6. Manual sections, as reference, not a novel:
   - [Properties](https://mpv.io/manual/stable/#properties)
   - [List of input commands](https://mpv.io/manual/stable/#list-of-input-commands)
   - Options: `hwdec`, `vo`, `gpu-api`, `wid`, `config-dir`, `osc`, `stream-record`

### During phase 2–3

7. `track-list` node format
8. WinUI 3 HWND interop + DPI
9. SMTC / Compact Overlay samples

### During phase 5–8

10. mpv cache / demuxer-max-bytes / network options
11. `stream-record` behavior and limits
12. Look at **mpv-android** and **mpv.net** as *products*, not code to fork

### Do not

- Fork mpv.net and “skin it.” You will inherit someone else’s UI model.
- Copy a random `Mpv.NET` sample and never read `client.h`.
- Read FFmpeg `ffplay.c` unless you are curious. It is no longer on the critical path.

---

## 11. Glossary

| Term | Meaning |
|---|---|
| libmpv | mpv built as a library; C API in `client.h` |
| `mpv_handle` | One player instance |
| Command | An action (`loadfile`, `seek`) |
| Property | A readable/writable value (`time-pos`, `pause`, `hwdec-current`) |
| Option | A property that often must be set *before* `mpv_initialize` |
| `wid` | Native parent window handle for embedding |
| Render API | Alternate embed: you call `mpv_render_context_render` each frame |
| VO | Video output (`gpu-next`) |
| AO | Audio output (`wasapi`) |
| hwdec | Hardware decode (`d3d11va` → NVDEC on your GPU) |
| OSC | mpv’s built-in on-screen controller (we disable it) |
| OSD | On-screen text (we mostly replace with WinUI) |
| `keep-open` | Stay on the last frame at EOF |
| `stream-record` | Remux the incoming stream to a file while playing |
| PlayerHost | Our C# façade over libmpv; the IPTV app uses this |
| Shell | A GUI that hosts PlayerHost (desktop player, IPTV, later Android) |
| SMTC | System Media Transport Controls |
| Compact Overlay | Windows PiP window mode |
| Isolated config | Our AppData mpv dir, not the user’s global mpv.conf |

---

## 12. Open questions (not blocking phase 0)

- Exact libmpv build (shinchiro release vs self-build) — pick the current shinchiro `libmpv` zip unless we need a custom patch
- Unpackaged WinUI vs MSIX when we ship
- Whether on-video fade controls are worth the HWND z-order pain, or we keep controls under the picture at first
- SQLite vs JSON for resume
- Whether IPTV lives in the same exe (mode switch) or a second exe (cleaner)

---

## 13. The short version

A player is still a **clock with queues**. We are not writing that clock. **libmpv is the clock.**

We write:

1. A thin **PlayerHost** around `client.h`
2. A **WinUI 3** desktop app that gives libmpv an HWND and draws everything else
3. Isolated NVIDIA/D3D11 defaults
4. Later, an **IPTV** shell that calls the same host
5. Much later, Android with the same command vocabulary

We start at phase 0: WinUI project + `libmpv-2.dll` loads + API version printed.

The things that will actually hurt us are **destroy order, config isolation, async load, DPI on the HWND, and treating PiP as a second player.** Everything else is app work.

When you are ready, we do phase 0 in the repo.
