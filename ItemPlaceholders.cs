using System.Text.RegularExpressions;

namespace SimpleTranslatorCS;

/// <summary>
/// 物品名占位符解析：${24位mongoId} 取物品全名（locale 键 "{id} Name"），
/// ${24位mongoId|short} 取物品短名（locale 键 "{id} ShortName"）。
/// 查找顺序：primary（目标语言字典）-> fallback（英文字典）；
/// 都找不到时回退为裸 id，并把 id 记入 unresolvedIds（供日志/报告反馈）。
/// 不依赖任何 SPT 类型，便于单元测试。
/// </summary>
internal static class ItemPlaceholders
{
    public static readonly Regex Pattern = new(@"\$\{([0-9a-fA-F]{24})(\|short)?\}", RegexOptions.Compiled);

    public static string Resolve(
        string text,
        IReadOnlyDictionary<string, string>? primary,
        IReadOnlyDictionary<string, string>? fallback,
        ICollection<string>? unresolvedIds = null)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("${"))
        {
            return text;
        }

        return Pattern.Replace(text, match =>
        {
            var tpl = match.Groups[1].Value;
            var localeKey = tpl + (match.Groups[2].Success ? " ShortName" : " Name");

            if (primary != null && primary.TryGetValue(localeKey, out var name) && !string.IsNullOrEmpty(name))
            {
                return name;
            }
            if (fallback != null && fallback.TryGetValue(localeKey, out name) && !string.IsNullOrEmpty(name))
            {
                return name;
            }

            unresolvedIds?.Add(tpl);
            return tpl;
        });
    }
}
