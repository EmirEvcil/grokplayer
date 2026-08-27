namespace Grok.Player.Core.Player;

public static class LivePlayback
{
    public const double CatchUpSlackSeconds = 4;
    public const double AtLiveSlackSeconds = 3;
    public const double FollowLiveSlackSeconds = 12;
    public const double LiveLeadSeconds = 2;
    public const double DvrKeepSeconds = 180;
    public const double DvrCapThresholdSeconds = 210;
    public const int MinPreviewBytes = 800;

    public static double TipSeconds(double position, double liveEdge, double cacheEnd) =>
        Max(0, position, liveEdge, cacheEnd);

    public static double SnapTargetSeconds(double position, double liveEdge, double cacheEnd)
    {
        var tip = TipSeconds(position, liveEdge, cacheEnd);
        return Math.Max(0, tip - LiveLeadSeconds);
    }

    public static bool NeedsCatchUp(double position, double tip, double slackSeconds = CatchUpSlackSeconds) =>
        tip > position + Math.Max(0.05, slackSeconds);

    public static bool IsAtLive(double position, double tip, double slackSeconds = AtLiveSlackSeconds) =>
        tip <= 0 || position >= tip - Math.Max(0.05, slackSeconds);

    public static bool CanKeepFollowing(double position, double tip) =>
        IsAtLive(position, tip, FollowLiveSlackSeconds);

    public static bool ShouldCapDvr(double tip) => tip >= DvrCapThresholdSeconds;

    public static double WindowStart(double tip) =>
        ShouldCapDvr(tip) ? Math.Max(0, tip - DvrKeepSeconds) : 0;

    public static double ClampToWindow(double position, double tip)
    {
        var start = WindowStart(tip);
        var end = Math.Max(start, tip);
        if (double.IsNaN(position) || double.IsInfinity(position))
        {
            return end;
        }

        return Math.Clamp(position, start, end);
    }

    public static bool IsUsableStill(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length < MinPreviewBytes)
            {
                return false;
            }

            Span<byte> header = stackalloc byte[4];
            using var stream = info.OpenRead();
            var read = stream.Read(header);
            return read >= 3 &&
                   (header[0] == 0xFF && header[1] == 0xD8 ||
                    header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E ||
                    header[0] == (byte)'B' && header[1] == (byte)'M');
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static double Max(double a, double b, double c, double d)
    {
        var x = a > b ? a : b;
        x = x > c ? x : c;
        return x > d ? x : d;
    }
}
