using System.Text.RegularExpressions;

namespace SimpleTranslatorCS;

internal sealed class WorkbenchSourceIndex
{
    public const string BaseGroup = "base-game";
    private static readonly Regex HexId = new("(?<![0-9a-fA-F])[0-9a-fA-F]{24}(?![0-9a-fA-F])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly Dictionary<string, string> _owners;

    private WorkbenchSourceIndex(Dictionary<string, string> owners) => _owners = owners;

    public string GroupFor(string localeKey)
    {
        var split = localeKey.IndexOf(' ');
        var id = split > 0 ? localeKey[..split] : localeKey;
        return _owners.TryGetValue(id, out var owner) ? owner : BaseGroup;
    }

    public static WorkbenchSourceIndex Build(string modPath, IEnumerable<string> candidateKeys)
    {
        var candidates = new HashSet<string>(candidateKeys.Select(ExtractId).Where(IsHexId), StringComparer.OrdinalIgnoreCase);
        if (candidates.Count == 0 || string.IsNullOrWhiteSpace(modPath))
            return new WorkbenchSourceIndex(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var parent = Directory.GetParent(modPath);
        var modsRoot = parent?.FullName;
        if (parent == null || !parent.Name.Equals("mods", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(modsRoot) || !Directory.Exists(modsRoot))
            return new WorkbenchSourceIndex(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        string[] directories;
        try
        {
            directories = Directory.EnumerateDirectories(modsRoot)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch
        {
            return new WorkbenchSourceIndex(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        var evidence = new Dictionary<string, Dictionary<string, (int MaxScore, int Occurrences)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            if (Path.GetFullPath(directory).Equals(Path.GetFullPath(modPath), StringComparison.OrdinalIgnoreCase))
                continue;
            var group = Path.GetFileName(directory);
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                    .Where(path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                   || path.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase))
                    .Where(path => !HasIgnoredSegment(directory, path))
                    .ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                try
                {
                    foreach (var line in File.ReadLines(file))
                    foreach (Match match in HexId.Matches(line))
                    {
                        var id = match.Value;
                        if (!candidates.Contains(id))
                            continue;
                        var score = DefinitionScore(line, id);
                        if (!evidence.TryGetValue(id, out var groups))
                            evidence[id] = groups = new Dictionary<string, (int MaxScore, int Occurrences)>(StringComparer.OrdinalIgnoreCase);
                        groups[group] = groups.TryGetValue(group, out var current)
                            ? (Math.Max(current.MaxScore, score), current.Occurrences + 1)
                            : (score, 1);
                    }
                }
                catch
                {
                    // Broken or locked third-party files are skipped; their keys remain in the base/unassigned group.
                }
            }
        }

        var owners = evidence.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.OrderByDescending(group => group.Value.MaxScore)
                .ThenByDescending(group => group.Value.Occurrences)
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase).First().Key,
            StringComparer.OrdinalIgnoreCase);
        return new WorkbenchSourceIndex(owners);
    }

    private static string ExtractId(string key)
    {
        var split = key.IndexOf(' ');
        return split > 0 ? key[..split] : key;
    }

    private static bool IsHexId(string value) => value.Length == 24 && value.All(Uri.IsHexDigit);

    private static int DefinitionScore(string line, string id)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("\"" + id + "\"", StringComparison.OrdinalIgnoreCase))
            return 8;
        if ((line.Contains("\"_id\"", StringComparison.OrdinalIgnoreCase)
             || line.Contains("\"id\"", StringComparison.OrdinalIgnoreCase))
            && line.Contains("\"" + id + "\"", StringComparison.OrdinalIgnoreCase))
            return 6;
        return 1;
    }

    private static bool HasIgnoredSegment(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative.Split('/').Any(part =>
            part.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || part.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || part.Equals("cache", StringComparison.OrdinalIgnoreCase)
            || part.Equals("logs", StringComparison.OrdinalIgnoreCase));
    }
}
