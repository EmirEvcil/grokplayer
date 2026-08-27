# GrokPlayer

Windows video player: WinUI 3 + C# around libmpv.

Local files, YouTube VOD/live via a companion Chrome extension, playlists, captions, downloads, and hardware decode (H.264 / HEVC / AV1 / AAC). No DRM.

## Build

1. Windows 10/11, .NET 8 SDK, Visual Studio 2022 with the Windows App SDK workload.
2. Fetch libmpv (LGPL build; not committed):

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools/fetch-libmpv.ps1
   ```

3. Open `GrokPlayer.slnx` and build **x64**.

The Chrome extension is in a separate repository: [grokplayer-extension](https://github.com/EmirEvcil/grokplayer-extension).

## License notes

libmpv is pulled at build time from the URL in `tools/libmpv-version.txt` (LGPLv2.1+).
