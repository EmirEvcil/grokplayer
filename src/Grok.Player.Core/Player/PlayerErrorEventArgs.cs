namespace Grok.Player.Core.Player;

public sealed class PlayerErrorEventArgs : EventArgs
{
    public PlayerErrorEventArgs(string message)
    {
        Message = message;
    }

    public string Message { get; }
}
