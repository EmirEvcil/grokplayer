using System.Text.Json;

namespace Grok.Player.App.Link;

internal static class SharedFolders
{
    public static List<string> List()
    {
        try
        {
            if (!File.Exists(PathFile()))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(PathFile()), LinkProtocol.Json)
                ?.Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Add(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        var full = Path.GetFullPath(path);
        var next = List();
        if (next.Any(item => string.Equals(item, full, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        next.Add(full);
        Save(next);
    }

    public static void Remove(string path)
    {
        Save(List().Where(item => !string.Equals(item, path, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    public static bool Allows(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        return List().Any(root =>
            string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }

    public static string? Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path);
            return Allows(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    private static void Save(List<string> paths)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathFile())!);
        File.WriteAllText(PathFile(), JsonSerializer.Serialize(paths, LinkProtocol.Json));
    }

    private static string PathFile() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GrokPlayer",
        "shared-folders.json");
}
