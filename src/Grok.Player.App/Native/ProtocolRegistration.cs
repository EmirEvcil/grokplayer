using Microsoft.Win32;

namespace Grok.Player.App.Native;

internal static class ProtocolRegistration
{
    public static void EnsureCurrentUser(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return;
        }

        try
        {
            using var grok = Registry.CurrentUser.CreateSubKey(@"Software\Classes\grokplayer");
            grok?.SetValue("", "URL:GrokPlayer");
            grok?.SetValue("URL Protocol", "");
            using var command = Registry.CurrentUser.CreateSubKey(@"Software\Classes\grokplayer\shell\open\command");
            command?.SetValue("", "\"" + exePath + "\" --stream \"%1\"");
        }
        catch (Exception)
        {
        }
    }
}
