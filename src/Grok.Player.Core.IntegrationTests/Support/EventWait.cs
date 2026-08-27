namespace Grok.Player.Core.IntegrationTests.Support;

internal static class EventWait
{
    public static void Until(Func<bool> condition, TimeSpan timeout, string because)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(20);
        }

        throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0.#}s: {because}");
    }
}
