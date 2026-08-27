using Grok.Player.Core.Media;
using Grok.Player.Core.Native;

namespace Grok.Player.Core.Download;

internal readonly record struct StreamDumpOptions(
    string Output,
    string Format,
    string? AudioFile,
    string? AudioLang,
    int MaxBitrate);

internal static class StreamDump
{
    internal static string? LastError { get; private set; }

    public static StreamDumpOptions CreateOptions(
        string output,
        string? audioUrl,
        string? audioLang,
        int maxBitrate)
    {
        var dest = output;
        var format = Path.GetExtension(output).ToLowerInvariant() switch
        {
            ".mp4" => "mp4",
            ".ts" => "mpegts",
            ".webm" => "webm",
            _ => "mkv"
        };
        return new StreamDumpOptions(
            dest,
            format,
            string.IsNullOrWhiteSpace(audioUrl) ? null : audioUrl.Trim(),
            string.IsNullOrWhiteSpace(audioLang) ? null : MediaLanguage.Normalize(audioLang),
            maxBitrate);
    }

    public static bool TryRemux(string video, string? audio, string output, CancellationToken token) =>
        TryDump(video, output, userAgent: null, audioLang: null, maxBitrate: 0, token, audio);

    public static bool TryDump(
        string url,
        string output,
        string? userAgent,
        string? audioLang,
        int maxBitrate,
        CancellationToken token,
        string? audioUrl = null)
    {
        LastError = null;
        if (!MpvNative.TryFindLibrary(out _))
        {
            LastError = "libmpv not found";
            return false;
        }

        var opts = CreateOptions(output, audioUrl, audioLang, maxBitrate);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(opts.Output)!);
            using var mpv = new MpvNative();
            mpv.SetOption("config", "no");
            mpv.SetOption("vo", "null");
            mpv.SetOption("ao", "null");
            mpv.SetOption("osc", "no");
            mpv.SetOption("ytdl", "no");
            mpv.SetOption("idle", "once");
            mpv.SetOption("keep-open", "no");
            mpv.SetOption("force-window", "no");
            mpv.SetOption("o", opts.Output);
            mpv.SetOption("of", opts.Format);
            mpv.SetOption("ovc", "copy");
            mpv.SetOption("oac", "copy");
            if (opts.MaxBitrate > 0)
            {
                mpv.SetOption("hls-bitrate", opts.MaxBitrate.ToString());
            }

            if (!string.IsNullOrWhiteSpace(opts.AudioLang))
            {
                mpv.SetOption("alang", opts.AudioLang + ",en");
            }

            if (!string.IsNullOrWhiteSpace(opts.AudioFile))
            {
                TryOption(mpv, "audio-files", opts.AudioFile);
                TryOption(mpv, "audio-file", opts.AudioFile);
            }

            if (!string.IsNullOrWhiteSpace(userAgent))
            {
                mpv.SetOption("user-agent", userAgent);
            }

            mpv.SetOption("referrer", "https://www.youtube.com");
            mpv.SetOption("http-header-fields", "Referer: https://www.youtube.com,Origin: https://www.youtube.com");
            mpv.SetOption("network-timeout", "20");
            mpv.Initialize();
            if (!string.IsNullOrWhiteSpace(opts.AudioFile))
            {
                TryProperty(mpv, "audio-files", opts.AudioFile);
            }

            mpv.Command("loadfile", url, "replace");
            if (!string.IsNullOrWhiteSpace(opts.AudioFile))
            {
                try
                {
                    mpv.Command("audio-add", opts.AudioFile);
                }
                catch (MpvException)
                {
                }
            }
            var until = DateTime.UtcNow.AddMinutes(30);
            while (DateTime.UtcNow < until)
            {
                token.ThrowIfCancellationRequested();
                var ev = mpv.WaitEvent(0.25);
                if (ev.Id == MpvEventId.EndFile)
                {
                    var ok = ev.EndFileReason != MpvEndFileReason.Error && HasOutput(opts.Output);
                    if (!ok)
                    {
                        LastError = "end-file " + ev.EndFileReason + " err=" + ev.EndFileError +
                                    " size=" + (File.Exists(opts.Output) ? new FileInfo(opts.Output).Length : 0);
                    }

                    return ok;
                }
            }

            var exists = HasOutput(opts.Output);
            if (!exists)
            {
                LastError = "timeout without output";
            }

            return exists;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    private static bool HasOutput(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 1024;

    private static void TryOption(MpvNative mpv, string name, string value)
    {
        try
        {
            mpv.SetOption(name, value);
        }
        catch (MpvException)
        {
        }
    }

    private static void TryProperty(MpvNative mpv, string name, string value)
    {
        try
        {
            mpv.SetPropertyString(name, value);
        }
        catch (MpvException)
        {
        }
    }
}
