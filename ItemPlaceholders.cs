using System.Text.RegularExpressions;

namespace SimpleTranslatorCS;

/// <summary>
/// 物品名占位符解析：${24位mongoId} 取物品全名（locale 键 "{id} Name"），
/// ${24位mongoId|short} 取物品短名（locale 键 "{id} ShortName"）。
/// 查找顺序：primary（目标语言字典）-> fallback（英文字典）；
/// 都找不到时回退为裸 id，并把 id 记入 unresolvedIds（供日志/报告反馈）。
/// 若查到的名字本身仍含 ${...}，递归继续解析（见 <see cref="MaxResolveDepth"/>）。
/// 不依赖任何 SPT 类型，便于单元测试。
/// </summary>
internal static class ItemPlaceholders
{
    /// <summary>
    /// 递归解析的最大深度，防止 A↔B 循环引用导致死循环/栈溢出。
    /// 取 8 的依据：真实翻译里"名字再嵌占位符"极少超过 2~3 层（任务文本 -> 物品名 -> 材料名），
    /// 8 层足以覆盖合理场景，又能在循环引用时快速止损。
    /// </summary>
    public const int MaxResolveDepth = 8;

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

        // 缺失 id 去重：同一键的同一条缺失 id 只报告一次（直接出现与递归出现不重复计数）
        var reported = unresolvedIds != null ? new HashSet<string>(unresolvedIds) : null;
        return ResolveInner(text, primary, fallback, unresolvedIds, reported, depth: 0);
    }

    private static string ResolveInner(
        string text,
        IReadOnlyDictionary<string, string>? primary,
        IReadOnlyDictionary<string, string>? fallback,
        ICollection<string>? unresolvedIds,
        HashSet<string>? reported,
        int depth)
    {
        return Pattern.Replace(text, match =>
        {
            var tpl = match.Groups[1].Value;
            var localeKey = tpl + (match.Groups[2].Success ? " ShortName" : " Name");

            string? name = null;
            if (primary != null && primary.TryGetValue(localeKey, out var p) && !string.IsNullOrEmpty(p))
            {
                name = p;
            }
            else if (fallback != null && fallback.TryGetValue(localeKey, out var f) && !string.IsNullOrEmpty(f))
            {
                name = f;
            }

            if (name == null)
            {
                AddUnresolved(unresolvedIds, reported, tpl);
                return tpl;
            }

            if (!name.Contains("${"))
            {
                return name;
            }

            if (depth + 1 >= MaxResolveDepth)
            {
                // 到达深度上限（典型为循环引用）：保留未解析的内层占位符，其 id 记为未解析
                foreach (Match inner in Pattern.Matches(name))
                {
                    AddUnresolved(unresolvedIds, reported, inner.Groups[1].Value);
                }
                return name;
            }

            return ResolveInner(name, primary, fallback, unresolvedIds, reported, depth + 1);
        });
    }

    private static void AddUnresolved(ICollection<string>? unresolvedIds, HashSet<string>? reported, string id)
    {
        if (unresolvedIds == null || reported == null)
        {
            return;
        }
        if (reported.Add(id))
        {
            unresolvedIds.Add(id);
        }
    }
}
