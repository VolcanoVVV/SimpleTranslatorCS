using System.Collections.ObjectModel;

namespace SimpleTranslatorCS;

/// <summary>
/// 多文件词条合并结果：合并后的原始（未解析占位符）键值 + 每个键的覆盖链 provenance。
/// 不依赖任何 SPT 类型；provenance 供 SimpleTranslator 的冲突报告/占位符归属与后续 T4 的生效报告复用。
/// </summary>
internal sealed class MergedTranslations
{
    /// <summary>合并后的原始键值（同键后文件覆盖前文件），键比较器与目标容器一致</summary>
    public required IReadOnlyDictionary<string, string> Values { get; init; }

    /// <summary>键 -> 完整覆盖链（所有定义过该键的文件，按加载顺序，最后一个为胜出文件）</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> OverrideChains { get; init; }

    /// <summary>该键的胜出文件（覆盖链最后一个）；键不存在时返回 null</summary>
    public string? WinningFile(string key) =>
        OverrideChains.TryGetValue(key, out var chain) && chain.Count > 0 ? chain[^1] : null;
}

/// <summary>
/// 全量合并器：把按稳定排序（即优先级）排列的多个翻译文件合并为一个字典。
/// 排序即优先级——同键后文件覆盖前文件；比较器用 <see cref="TranslationFiles.KeyComparer"/>，
/// 与目标 Global 容器一致；4.1 忽略大小写，4.0 区分大小写。不依赖任何 SPT 类型。
/// </summary>
internal static class TranslationMerger
{
    public static MergedTranslations Merge(IEnumerable<(string File, IReadOnlyDictionary<string, string> Entries)> files)
    {
        var values = new Dictionary<string, string>(TranslationFiles.KeyComparer);
        var chains = new Dictionary<string, List<string>>(TranslationFiles.KeyComparer);

        foreach (var (file, entries) in files)
        {
            foreach (var kvp in entries)
            {
                values[kvp.Key] = kvp.Value;

                if (!chains.TryGetValue(kvp.Key, out var chain))
                {
                    chain = new List<string>();
                    chains[kvp.Key] = chain;
                }
                // 同文件内经比较器折叠的同键变体（如仅大小写不同）只在链上记一次来源
                if (chain.Count == 0 || chain[^1] != file)
                {
                    chain.Add(file);
                }
            }
        }

        return new MergedTranslations
        {
            Values = new ReadOnlyDictionary<string, string>(values),
            OverrideChains = new ReadOnlyDictionary<string, IReadOnlyList<string>>(chains.ToDictionary(
                p => p.Key,
                p => (IReadOnlyList<string>)p.Value.AsReadOnly(),
                TranslationFiles.KeyComparer)),
        };
    }

    /// <summary>一次构建始终从同一份原始合并视图解析，不读取已解析后写回的值。</summary>
    public static void ApplyTo(
        Dictionary<string, string> target,
        MergedTranslations merged,
        IReadOnlyDictionary<string, string>? fallback,
        Action<string, string>? unresolved = null)
    {
        var source = new Dictionary<string, string>(target, target.Comparer);
        foreach (var pair in merged.Values)
            source[pair.Key] = pair.Value;

        foreach (var pair in merged.Values)
        {
            var missing = unresolved == null ? null : new List<string>();
            target[pair.Key] = ItemPlaceholders.Resolve(pair.Value, source, fallback, missing);
            if (missing != null)
                foreach (var id in missing)
                    unresolved!(pair.Key, id);
        }
    }
}
