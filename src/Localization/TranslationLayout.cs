using System.Security.Cryptography;
using System.Text;

namespace SimpleTranslatorCS;

internal static class TranslationLayout
{
    internal static string Mode(string relative)
    {
        relative = LocalPath(relative);
        if (relative.Split('/').Any(segment => segment.Equals("dialogue", StringComparison.OrdinalIgnoreCase)))
            return "manual";
        if (relative.StartsWith("MT/", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("_workbench/10-machine.jsonc", StringComparison.OrdinalIgnoreCase))
            return "machine";
        if (relative.StartsWith("API/", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("_workbench/20-ai.jsonc", StringComparison.OrdinalIgnoreCase))
            return "ai";
        if (relative.StartsWith("Manual/", StringComparison.OrdinalIgnoreCase))
            return "manual";
        var stem = Path.GetFileNameWithoutExtension(relative);
        if (stem.EndsWith("_MT", StringComparison.OrdinalIgnoreCase)) return "machine";
        if (stem.EndsWith("_API", StringComparison.OrdinalIgnoreCase)) return "ai";
        return "manual";
    }

    internal static int Rank(string relative) => Mode(relative) switch
    {
        "machine" => 0,
        "ai" => 1,
        _ => 2
    };

    // Compatibility files load first within their own mode; flat files with a suffix load last.
    internal static int Placement(string relative)
    {
        if (relative.StartsWith("@legacy/", StringComparison.Ordinal)) return 0;
        if (relative.StartsWith("_workbench/", StringComparison.OrdinalIgnoreCase) && IsLegacyOutput(relative)) return 1;
        return IsFlatNamedFile(relative) ? 3 : 2;
    }

    internal static bool MayBeTracked(string relative) => IsFlatNamedFile(relative) || IsLegacyOutput(relative);

    private static bool IsFlatNamedFile(string relative)
    {
        if (relative.Contains('/')) return false;
        var stem = Path.GetFileNameWithoutExtension(relative);
        return stem.EndsWith("_MT", StringComparison.OrdinalIgnoreCase) ||
               stem.EndsWith("_API", StringComparison.OrdinalIgnoreCase) ||
               stem.EndsWith("_Manual", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsLegacyOutput(string relative) =>
        !relative.StartsWith("@legacy/", StringComparison.Ordinal) &&
        (relative.Equals("MT/generated.jsonc", StringComparison.OrdinalIgnoreCase) ||
         relative.Equals("API/generated.jsonc", StringComparison.OrdinalIgnoreCase) ||
         relative.Equals("Manual/workbench.jsonc", StringComparison.OrdinalIgnoreCase) ||
         relative.Equals("_workbench/10-machine.jsonc", StringComparison.OrdinalIgnoreCase) ||
         relative.Equals("_workbench/20-ai.jsonc", StringComparison.OrdinalIgnoreCase));

    internal static string FileName(string sourceGroup, string mode)
    {
        var stem = sourceGroup switch
        {
            WorkbenchSourceIndex.BaseGroup => "原版游戏",
            "other-locales" => "其他语言表文本",
            _ => SafeModName(sourceGroup)
        };
        var suffix = mode switch
        {
            "ai" => "API",
            "machine" => "MT",
            "manual" => "Manual",
            _ => throw new ArgumentException("未知的译文保存类型。", nameof(mode))
        };
        return stem + "_" + suffix + ".jsonc";
    }

    internal static string CombinedApiFileName(IEnumerable<string> sourceGroups)
    {
        var groups = sourceGroups.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(group => group, StringComparer.Ordinal).ToArray();
        if (groups.Length == 1) return FileName(groups[0], "ai");
        var joined = string.Join(".", groups.Select(group => FileName(group, "ai")[..^10]));
        // Long combinations keep an identifiable prefix and a hash instead of exceeding filesystem limits.
        return (Encoding.UTF8.GetByteCount(joined) <= 180 ? joined : SafeModName(joined)) + "_API.jsonc";
    }

    private static string SafeModName(string sourceGroup)
    {
        const string invalid = "<>:\"/\\|?*";
        var stem = string.Concat(sourceGroup.Select(ch =>
            char.IsControl(ch) || invalid.Contains(ch) ? '_' : ch)).Trim().TrimEnd('.');
        // Bound the UTF-8 name on filesystems with a 255-byte component limit.
        var firstSegment = stem.Split('.')[0].TrimEnd().ToUpperInvariant();
        var device = firstSegment is "CON" or "PRN" or "AUX" or "NUL" ||
                     firstSegment.Length == 4 && (firstSegment.StartsWith("COM", StringComparison.Ordinal) ||
                                                  firstSegment.StartsWith("LPT", StringComparison.Ordinal)) &&
                     "123456789¹²³".Contains(firstSegment[3]);
        if (device) stem = "模组-" + stem;
        if (stem == sourceGroup && stem.Length > 0 && Encoding.UTF8.GetByteCount(stem) <= 120 &&
            stem is not ("原版游戏" or "其他语言表文本"))
            return stem;
        var prefix = new StringBuilder();
        var bytes = 0;
        foreach (var rune in stem.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 100) break;
            prefix.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceGroup)))[..12];
        return (prefix.Length == 0 ? "模组" : prefix.ToString()) + "-" + identity;
    }

    private static string LocalPath(string relative) =>
        relative.StartsWith("@legacy/", StringComparison.Ordinal) ? relative[8..] : relative;
}
