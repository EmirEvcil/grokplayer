namespace Grok.Player.Core.Video;

public sealed class VideoResizeModel
{
    public event Action? Changed;

    public VideoResizeSettings Applied { get; private set; } = VideoResizeSettings.Default;

    public VideoResizeSettings Draft { get; private set; } = VideoResizeSettings.Default;

    public VideoResizeSettings Live { get; private set; } = VideoResizeSettings.Default;

    public bool HasBeenPushed { get; private set; }

    public void SetPolicy(VideoResizePolicy policy) => Patch(Draft with { Policy = policy });

    public void SetSizing(VideoSizingMode sizing) => Patch(Draft with { Sizing = sizing });

    public void SetMultiplier(VideoScaleMultiplier multiplier) => Patch(Draft with { Multiplier = multiplier });

    public bool SetCustomMultiplier(double value)
    {
        if (!VideoResizeSpec.IsValidMultiplier(value))
        {
            return false;
        }

        Patch(Draft with { Multiplier = VideoScaleMultiplier.Custom, CustomMultiplier = value });
        return true;
    }

    public bool SetCustomWidth(int width, int sourceW, int sourceH)
    {
        if (!VideoResizeSpec.IsValidPixels(width))
        {
            return false;
        }

        var height = Draft.KeepCustomAspect
            ? VideoResizeSpec.HeightFromWidth(width, Draft, sourceW, sourceH)
            : Draft.CustomHeight;
        Patch(Draft with { CustomWidth = width, CustomHeight = height });
        return true;
    }

    public bool SetCustomHeight(int height, int sourceW, int sourceH)
    {
        if (!VideoResizeSpec.IsValidPixels(height))
        {
            return false;
        }

        var width = Draft.KeepCustomAspect
            ? VideoResizeSpec.WidthFromHeight(height, Draft, sourceW, sourceH)
            : Draft.CustomWidth;
        Patch(Draft with { CustomWidth = width, CustomHeight = height });
        return true;
    }

    public void SetKeepCustomAspect(bool keep, int sourceW, int sourceH)
    {
        var width = Draft.CustomWidth;
        var height = keep
            ? VideoResizeSpec.HeightFromWidth(width, Draft, sourceW, sourceH)
            : Draft.CustomHeight;
        Patch(Draft with { KeepCustomAspect = keep, CustomHeight = height });
    }

    public void SetAspect(VideoAspectMode aspect) => Patch(Draft with { Aspect = aspect });

    public bool SetCustomAspect(int x, int y)
    {
        if (x < 1 || y < 1)
        {
            return false;
        }

        Patch(Draft with { Aspect = VideoAspectMode.Custom, CustomAspectX = x, CustomAspectY = y });
        return true;
    }

    public bool SetShortcutStep(double step)
    {
        var clamped = VideoResizeSpec.ClampShortcutStep(step);
        if (Math.Abs(Draft.ShortcutStep - clamped) < 0.0001)
        {
            return false;
        }

        var next = Draft with { ShortcutStep = clamped };
        Draft = next;
        Applied = Applied with { ShortcutStep = clamped };
        Live = Live with { ShortcutStep = clamped };
        Changed?.Invoke();
        return true;
    }

    public bool NudgeHorizontal(int direction)
    {
        var next = VideoResizeSpec.ClampAdjust(Live.AdjustX + Math.Sign(direction) * Live.ShortcutStep);
        if (Math.Abs(next - Live.AdjustX) < 0.0001)
        {
            return false;
        }

        SetAdjust(next, Live.AdjustY);
        return true;
    }

    public bool NudgeVertical(int direction)
    {
        var next = VideoResizeSpec.ClampAdjust(Live.AdjustY + Math.Sign(direction) * Live.ShortcutStep);
        if (Math.Abs(next - Live.AdjustY) < 0.0001)
        {
            return false;
        }

        SetAdjust(Live.AdjustX, next);
        return true;
    }

    public bool ResetAdjust()
    {
        if (Math.Abs(Live.AdjustX - 1) < 0.0001 && Math.Abs(Live.AdjustY - 1) < 0.0001)
        {
            return false;
        }

        SetAdjust(1, 1);
        return true;
    }

    public void Preview()
    {
        Live = Draft;
        HasBeenPushed = true;
        Changed?.Invoke();
    }

    public void Apply()
    {
        Applied = Draft;
        Live = Draft;
        HasBeenPushed = true;
        Changed?.Invoke();
    }

    public void Reset()
    {
        Draft = VideoResizeSettings.Default;
        Applied = Draft;
        Live = Draft;
        HasBeenPushed = true;
        Changed?.Invoke();
    }

    private void SetAdjust(double x, double y)
    {
        Draft = Draft with { AdjustX = x, AdjustY = y };
        Applied = Applied with { AdjustX = x, AdjustY = y };
        Live = Live with { AdjustX = x, AdjustY = y };
        HasBeenPushed = true;
        Changed?.Invoke();
    }

    private void Patch(VideoResizeSettings next)
    {
        if (next == Draft)
        {
            return;
        }

        Draft = next;
        Changed?.Invoke();
    }
}
