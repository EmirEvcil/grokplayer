using Grok.Player.Core.Player;

namespace Grok.Player.Core.Playlist;

public enum DropAction
{
    PlayFirstEnqueueRest,
    EnqueueAll
}

public static class DropPolicy
{
    public static DropAction ForState(PlayerState state) =>
        state is PlayerState.Playing or PlayerState.Paused or PlayerState.Opening
            ? DropAction.EnqueueAll
            : DropAction.PlayFirstEnqueueRest;

    public static IReadOnlyList<string> FilterSupported(IEnumerable<string> paths) =>
        paths.Where(MediaFiles.IsSupported).ToArray();
}
