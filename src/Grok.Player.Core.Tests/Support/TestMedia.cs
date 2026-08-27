namespace Grok.Player.Core.Tests.Support;

internal static class TestMedia
{
    public static string CreateTempFile(string? name = null)
    {
        var path = Path.Combine(Path.GetTempPath(), name ?? $"grok-player-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70]);
        return path;
    }
}
