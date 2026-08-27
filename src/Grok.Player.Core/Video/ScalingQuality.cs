namespace Grok.Player.Core.Video;

public enum ScalingPreset
{
    Performance,
    Balanced,
    Cinema,
    Sharp,
    Custom
}

public enum ScaleKernel
{
    FastBilinear,
    Bilinear,
    Bicubic,
    Lanczos,
    Gaussian,
    Spline
}

public enum ScaleStrength
{
    Off,
    Low,
    Medium,
    High
}

public sealed record ScalingQualitySettings(
    ScalingPreset Preset,
    ScaleKernel Upscale,
    ScaleKernel Downscale,
    ScaleKernel Chroma,
    ScaleStrength AntiRing,
    ScaleStrength Deband)
{
    public static ScalingQualitySettings Default => ScalingQualitySpec.ForPreset(ScalingPreset.Balanced);
}

public static class ScalingQualitySpec
{
    public static ScalingQualitySettings ForPreset(ScalingPreset preset) => preset switch
    {
        ScalingPreset.Performance => new(
            ScalingPreset.Performance,
            ScaleKernel.FastBilinear,
            ScaleKernel.FastBilinear,
            ScaleKernel.FastBilinear,
            ScaleStrength.Off,
            ScaleStrength.Off),
        ScalingPreset.Cinema => new(
            ScalingPreset.Cinema,
            ScaleKernel.Spline,
            ScaleKernel.Gaussian,
            ScaleKernel.Bicubic,
            ScaleStrength.Medium,
            ScaleStrength.Medium),
        ScalingPreset.Sharp => new(
            ScalingPreset.Sharp,
            ScaleKernel.Lanczos,
            ScaleKernel.Lanczos,
            ScaleKernel.Lanczos,
            ScaleStrength.High,
            ScaleStrength.Off),
        _ => new(
            ScalingPreset.Balanced,
            ScaleKernel.Bicubic,
            ScaleKernel.Bicubic,
            ScaleKernel.Bilinear,
            ScaleStrength.Low,
            ScaleStrength.Low)
    };

    public static string MpvName(ScaleKernel kernel) => kernel switch
    {
        ScaleKernel.FastBilinear => "bilinear",
        ScaleKernel.Bilinear => "triangle",
        ScaleKernel.Bicubic => "bicubic",
        ScaleKernel.Lanczos => "lanczos",
        ScaleKernel.Gaussian => "gaussian",
        ScaleKernel.Spline => "spline36",
        _ => "bicubic"
    };

    public static double AntiRing(ScaleStrength strength) => strength switch
    {
        ScaleStrength.Low => 0.3,
        ScaleStrength.Medium => 0.6,
        ScaleStrength.High => 1.0,
        _ => 0.0
    };

    public static bool DebandEnabled(ScaleStrength strength) => strength != ScaleStrength.Off;

    public static int DebandIterations(ScaleStrength strength) => strength switch
    {
        ScaleStrength.Low => 1,
        ScaleStrength.Medium => 2,
        ScaleStrength.High => 4,
        _ => 1
    };

    public static double DebandThreshold(ScaleStrength strength) => strength switch
    {
        ScaleStrength.Low => 32,
        ScaleStrength.Medium => 48,
        ScaleStrength.High => 64,
        _ => 48
    };

    public static double DebandRange(ScaleStrength strength) => strength switch
    {
        ScaleStrength.Low => 12,
        ScaleStrength.High => 16,
        _ => 16
    };

    public static double DebandGrain(ScaleStrength strength) => strength switch
    {
        ScaleStrength.Low => 16,
        ScaleStrength.Medium => 32,
        ScaleStrength.High => 48,
        _ => 32
    };

    public static string Label(ScalingPreset preset) => preset switch
    {
        ScalingPreset.Performance => "Performance",
        ScalingPreset.Cinema => "Cinema",
        ScalingPreset.Sharp => "Sharp",
        ScalingPreset.Custom => "Custom",
        _ => "Balanced"
    };

    public static string Label(ScaleKernel kernel) => kernel switch
    {
        ScaleKernel.FastBilinear => "Fast bilinear",
        ScaleKernel.Bilinear => "Bilinear",
        ScaleKernel.Lanczos => "Lanczos",
        ScaleKernel.Gaussian => "Gaussian",
        ScaleKernel.Spline => "Spline",
        _ => "Bicubic"
    };

    public static string Label(ScaleStrength strength) => strength switch
    {
        ScaleStrength.Low => "Low",
        ScaleStrength.Medium => "Medium",
        ScaleStrength.High => "High",
        _ => "Off"
    };

    public static string KernelTip(ScaleKernel kernel) => kernel switch
    {
        ScaleKernel.FastBilinear =>
            "Fastest GPU bilinear. Soft and cheap. Best when the picture is already close to the window size.",
        ScaleKernel.Bilinear =>
            "Smoother linear interpolation. Still fast, a bit cleaner than the fast path.",
        ScaleKernel.Bicubic =>
            "Sharper than bilinear with moderate GPU cost. A solid everyday scaler.",
        ScaleKernel.Lanczos =>
            "Very sharp sinc window. Can ring on high-contrast edges. Higher GPU cost.",
        ScaleKernel.Gaussian =>
            "Soft and film-like. Hides aliasing; not for a crisp look.",
        ScaleKernel.Spline =>
            "High-quality spline36. Smooth and detailed with less ringing than Lanczos.",
        _ => ""
    };
}
