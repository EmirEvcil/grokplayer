namespace Grok.Player.Core.Player;

public sealed class PlayerHostOptions
{
    public bool Headless { get; init; }

    public bool HardwareDecode { get; init; } = true;

    public string Hwdec { get; init; } = "d3d11va-copy";

    public string VideoOutput { get; init; } = "gpu-next";

    public string GpuApi { get; init; } = "d3d11";

    public string AudioOutput { get; init; } = "wasapi";

    public nint WindowHandle { get; init; }

    public bool UseBackgroundEventLoop { get; init; } = true;

    public double InitialVolume { get; init; } = 100;

    public static PlayerHostOptions ForUserInterface(nint hwnd) => new()
    {
        Headless = false,
        HardwareDecode = true,
        Hwdec = "d3d11va-copy",
        VideoOutput = "gpu-next",
        WindowHandle = hwnd,
        UseBackgroundEventLoop = true
    };

    public static PlayerHostOptions ForAutomatedTests() => new()
    {
        Headless = true,
        HardwareDecode = false,
        VideoOutput = "null",
        AudioOutput = "null",
        UseBackgroundEventLoop = false
    };
}
