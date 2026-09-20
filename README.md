# GrokPlayer

Windows video player: WinUI 3 + C# around libmpv. Local files, YouTube and other streams via a companion Chrome extension, playlists, captions, downloads, hardware decode (H.264 / HEVC / AV1 / AAC), and a LAN link to GrokPlayer TV. No DRM.

Companion apps: [GrokPlayer TV](https://github.com/EmirEvcil/grokplayer-tv) · [Chrome extension](https://github.com/EmirEvcil/grokplayer-extension)

Current build: **1.1.13**. `grokplayer:` protocol so the extension can open the player.

## Features

### Playback
- libmpv (`gpu-next` + D3D11, `d3d11va-copy` hardware decode, WASAPI audio)
- Open files, folders, or drag-and-drop (video and MP3)
- Play / pause, seek, volume, speed
- Resume from last position
- Full screen (preserve aspect or stretch)
- Second player window / new instance for a playlist item
- `grokplayer://open?…` protocol from the browser extension

### Playlists
- Local playlist and Stream playlist (separate tabs)
- Add, remove, reorder, play, play in a new instance
- Stream playlist: download one or download all
- Drop files onto the window

### Streams
- Open or add a stream URL
- YouTube VOD and live (watch URL resolved in-app, or a playable URL from the extension)
- Kick, Twitch, Rumble, TikTok, Dailymotion, Instagram, and sniffed HLS / DASH / progressive
- Live: go to live edge
- Start / stop recording a live stream
- Catalog pages vs CDN hosts: playback query strings (e.g. Dailymotion `?sec=`) are kept

### Captions
- Embedded tracks and sidecar files (SRT / VTT / ASS and related)
- Load, add, merge, cycle, off
- Stream subtitles: off / on / subtitle-browser integration
- YouTube timed text and HLS caption renditions
- Caption style window and subtitle sync offset
- Language preference from the extension (auto / original / off)

### Picture and audio
- RTX Video Super Resolution (on/off)
- HDR: off, native HDR, RTX HDR
- Image adjust shortcuts (Ctrl+1–5)
- Audio track picker
- Equalizer with built-in presets
- Scaling / resize preferences

### Seek previews
- VOD storyboard / WebVTT preview atlas when the site exposes one
- Cached clip previews
- Live preview buffer while paused behind the edge

### Downloads
- Queue, progress, retry
- HLS playlist dump + ffmpeg mux
- Quality / height preference
- Download folder and related preferences
- Sidecar captions saved with the file when available

### Devices (LAN TV)
- Devices window: this PC, paired TVs, pin-above-player
- Share folders with GrokPlayer TV
- TV browses those folders and plays files over `/v1/file`
- TV can send a VOD to this PC
- Resume position shared on the link
- Link stays alive while the player is open

### UI
- Dark WinUI chrome, Mica backdrop
- Control panel (pin, opacity)
- Playlist pane toggle
- Preferences window (playback, downloads, subtitles, device, video, audio, …)
- Downloads window
- Resume prompt

## Build

1. Windows 10/11, .NET 8 SDK, Visual Studio 2022 with the Windows App SDK workload.
2. Fetch libmpv (LGPL build; not committed):

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools/fetch-libmpv.ps1
   ```

3. Open `GrokPlayer.slnx` and build **x64**.

Portable publish (registers the `grokplayer:` protocol):

```powershell
powershell -ExecutionPolicy Bypass -File tools/publish.ps1
```

Output: `dist/GrokPlayer`. Stop a running GrokPlayer process if that folder is locked.

## License notes

libmpv is pulled at build time from the URL in `tools/libmpv-version.txt` (LGPLv2.1+). DRM (Netflix, Disney+, Widevine, PlayReady) is out of scope.
