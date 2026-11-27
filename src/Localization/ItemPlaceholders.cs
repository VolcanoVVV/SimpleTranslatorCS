using System.Text;
using System.Text.RegularExpressions;

namespace SimpleTranslatorCS;

/// <summary>从固定原始视图解析物品名；循环、深度和展开预算超限均回退裸 ID。</summary>
internal static class ItemPlaceholders
{
    // 从根文本开始最多解引用八层物品名。
    public const int MaxResolveDepth = 8;
    public const int MaxExpandedCharacters = 65_536;
    public const int MaxSubstitutions = 4_096;
    public static readonly Regex Pattern = new(@"\$\{([0-9a-fA-F]{24})(\|short)?\}", RegexOptions.Compiled);

    public static string Resolve(string text, IReadOnlyDictionary<string, string>? primary,
        IReadOnlyDictionary<string, string>? fallback, ICollection<string>? unresolvedIds = null)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("${")) return text;
        var state = new ResolutionState(unresolvedIds);
        try { return ResolveInner(text, primary, fallback, state, 0); }
        catch (ExpansionLimitException)
        {
            // 不返回不完整句子，也不残留本 mod 的占位符。根原文长度不因回退而增长。
            return Pattern.Replace(text, match =>
            {
                var id = match.Groups[1].Value;
                state.Report(id);
                return id;
            });
        }
    }

    private static string ResolveInner(string text, IReadOnlyDictionary<string, string>? primary,
        IReadOnlyDictionary<string, string>? fallback, ResolutionState state, int depth)
    {
        var result = new StringBuilder(Math.Min(text.Length, MaxExpandedCharacters));
        var offset = 0;
        foreach (Match match in Pattern.Matches(text))
        {
            Append(text.Substring(offset, match.Index - offset));
            var id = match.Groups[1].Value;
            var key = id + (match.Groups[2].Success ? " ShortName" : " Name");
            if (--state.RemainingSubstitutions < 0) throw new ExpansionLimitException();
            string? name = null;
            if (primary != null && primary.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)) name = value;
            else if (fallback != null && fallback.TryGetValue(key, out value) && !string.IsNullOrEmpty(value)) name = value;

            if (name == null || depth >= MaxResolveDepth || !state.ActiveKeys.Add(key))
            {
                state.Report(id);
                Append(id);
            }
            else
            {
                try { Append(ResolveInner(name, primary, fallback, state, depth + 1)); }
                finally { state.ActiveKeys.Remove(key); }
            }
            offset = match.Index + match.Length;
        }
        Append(text.Substring(offset));
        return result.ToString();

        void Append(string part)
        {
            // 计算递归工作量而非仅最终长度，限制扇出导致的指数展开。
            if (part.Length > state.RemainingCharacters) throw new ExpansionLimitException();
            state.RemainingCharacters -= part.Length;
            result.Append(part);
        }
    }

    private sealed class ExpansionLimitException : Exception { }

    private sealed class ResolutionState(ICollection<string>? unresolved)
    {
        public int RemainingCharacters = MaxExpandedCharacters;
        public int RemainingSubstitutions = MaxSubstitutions;
        public readonly HashSet<string> ActiveKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _reported = new(unresolved ?? Array.Empty<string>());
        public void Report(string id)
        {
            if (_reported.Add(id)) unresolved?.Add(id);
        }
    }
}
