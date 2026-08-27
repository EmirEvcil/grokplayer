namespace Grok.Player.Core.Native;

public interface IMpvNative : IDisposable
{
    bool IsTerminated { get; }

    void SetOption(string name, string value);
    void SetOptionLong(string name, long value);
    void Initialize();
    void Command(params string[] args);
    void SetPropertyString(string name, string value);
    void SetPropertyFlag(string name, bool value);
    void SetPropertyDouble(string name, double value);
    void SetPropertyLong(string name, long value);
    string? GetPropertyString(string name);
    bool? GetPropertyFlag(string name);
    double? GetPropertyDouble(string name);
    long? GetPropertyLong(string name);
    void ObserveProperty(string name, MpvFormat format);
    MpvEvent WaitEvent(double timeoutSeconds);
    void Wakeup();
    void TerminateDestroy();
}
