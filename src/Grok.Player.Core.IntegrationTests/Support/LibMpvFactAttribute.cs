using Grok.Player.Core.Native;

namespace Grok.Player.Core.IntegrationTests.Support;

public sealed class LibMpvFactAttribute : FactAttribute
{
    public LibMpvFactAttribute()
    {
        if (!MpvNative.TryFindLibrary(out _))
        {
            Skip = "libmpv-2.dll is not available.";
        }
    }
}
