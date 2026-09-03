namespace Grok.Player.Core.Presentation;

public static class ActionFeedback
{
    public static string Skip(TimeSpan amount)
    {
        var span = amount < TimeSpan.Zero ? -amount : amount;
        if (span.TotalSeconds < 1)
        {
            return $"Skipping {span.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}s";
        }

        return $"Skipping {TimeDisplay.FormatSeek(span)}";
    }

    public static string Volume(double volume) =>
        $"Volume {Math.Clamp(Math.Round(volume), 0, 100):0}";

    public static string EqualizerEnabled(bool on) => on ? "Equalizer on" : "Equalizer off";

    public static string EqualizerPreset(string name) => $"EQ {name}";

    public static string EqualizerBand(string label, double value)
    {
        var rounded = Math.Clamp(Math.Round(value), -100, 100);
        var sign = rounded > 0 ? "+" : "";
        return $"EQ {label} {sign}{rounded:0}";
    }

    public static string VideoPicture(string label, double value) =>
        $"{label} {Math.Clamp(Math.Round(value), 0, 100):0}";

    public static string VideoFilter(string name, bool on) => on ? $"{name} on" : $"{name} off";

    public static string HdrMode(string label) => $"HDR {label}";

    public static string CapturedFrame() => "Captured frame";

    public static string SubtitleLoaded(string name) =>
        string.IsNullOrWhiteSpace(name) ? "Subtitle loaded" : $"Subtitle {name}";

    public static string SubtitleAdded(string name) =>
        string.IsNullOrWhiteSpace(name) ? "Subtitle added" : $"Added {name}";

    public static string SubtitlesMerged() => "Subtitles merged";

    public static string SubtitlesOff() => "Subtitles off";

    public static string SubtitleSync(double seconds)
    {
        var rounded = Math.Round(seconds, 3);
        var sign = rounded > 0 ? "+" : "";
        return $"Sync {sign}{rounded.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}s";
    }

    public static string SubtitleFont(string name) =>
        string.IsNullOrWhiteSpace(name) ? "Font" : $"Font {name}";

    public static string SubtitleSize(double size) => $"Size {Math.Clamp(Math.Round(size), 8, 200):0}";

    public static string Speed(double speed) =>
        $"Speed {speed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}x";

    public static string LoopPoint(string name, TimeSpan time) =>
        $"{name} {TimeDisplay.FormatSeek(time)}";

    public static string LoopCleared() => "A-B off";

    public static string Opened(int index, int count, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return count > 0 ? $"[{Math.Max(1, index)}/{count}]" : "Opened";
        }

        return count > 0
            ? $"[{Math.Max(1, index)}/{count}] {name}"
            : name;
    }

    public static string Added(int count) =>
        count <= 0 ? "Added files" : count == 1 ? "Added 1 file" : $"Added {count} files";

    public static string ScalingPreset(string name) => $"Scaling {name}";

    public static string ScaleKernel(string slot, string kernel) => $"{slot} {kernel}";

    public static string ScaleStrength(string name, string strength) => $"{name} {strength}";

    public static string ResizePolicy(string name) => $"Resize {name}";

    public static string ResizeSizing(string name) => name;

    public static string ResizeAspect(string name) => $"Aspect {name}";

    public static string ResizeSize(int width, int height) => $"Size {width}×{height}";

    public static string ResizeApplied() => "Resize applied";

    public static string ResizePreview() => "Resize preview";

    public static string ResizeReset() => "Resize reset";

    public static string ShortcutStep(double percent) =>
        $"Step {percent.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}%";

    public static string ImageWidth(double factor) => $"Width {Math.Round(factor * 100):0}%";

    public static string ImageHeight(double factor) => $"Height {Math.Round(factor * 100):0}%";

    public static string ImageReset() => "Image reset";

    public static string StreamAdded(string name) =>
        string.IsNullOrWhiteSpace(name) ? "Stream added" : $"Stream {name}";

    public static string GoLive() => "Live";

    public static string RecordingStarted() => "Recording";

    public static string RecordingStopped() => "Recording saved";

    public static string SeekTo(TimeSpan position, TimeSpan? duration)
    {
        var label = $"Seek {TimeDisplay.FormatSeek(position)}";
        if (duration is { } total && total.TotalSeconds > 0)
        {
            var percent = Math.Clamp(position.TotalSeconds / total.TotalSeconds * 100, 0, 100);
            return $"{label} ({percent:0}%)";
        }

        return label;
    }
}
