namespace Grok.Player.Core.Player;

public sealed record PlayerTrack(string Type, long Id, string Language, string Title, bool Selected, bool External);