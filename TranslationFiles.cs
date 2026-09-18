namespace SimpleTranslatorCS;

/// <summary>
/// 翻译文件的稳定枚举 + 与目标容器一致的键比较器。
/// 不依赖任何 SPT 类型，供测试项目直接覆盖；T3/T4 也会消费这里的接口。
/// </summary>
internal static class TranslationFiles
{
    /// <summary>
    /// 目标语言字典实际的键比较规则：
    /// 4.1 的 GlobalLocaleDictionary 实测为 OrdinalIgnoreCase（ILSpy 验证，见 docs/ARCHITECTURE_REVIEW.zh-CN.md），
    /// 4.0 的 Global 是普通 Dictionary&lt;string, string&gt;（默认 Ordinal）。
    /// 内部冲突追踪/合并字典必须用它，否则仅大小写不同的键在 4.1 下互相覆盖却被报告为不同键。
    /// </summary>
#if SPT40
    public static StringComparer KeyComparer { get; } = StringComparer.Ordinal;
#else
    public static StringComparer KeyComparer { get; } = StringComparer.OrdinalIgnoreCase;
#endif

    /// <summary>
    /// 递归枚举目录下的 .json/.jsonc 翻译文件（排除 dialogue 子目录），
    /// 按统一为 '/' 分隔的相对路径 Ordinal 排序返回，保证 Windows/Linux、跨机器结果一致。
    /// </summary>
    public static IReadOnlyList<string> EnumerateLocaleFiles(string dir)
    {
        return EnumerateSorted(dir)
            .Where(f => !IsUnderDialogueDir(RelativeSortKey(dir, f)))
            .ToList();
    }

    /// <summary>
    /// 枚举 dialogue 目录内的 .json/.jsonc 文件，排序规则与 <see cref="EnumerateLocaleFiles"/> 一致。
    /// </summary>
    public static IReadOnlyList<string> EnumerateDialogueFiles(string dialogueDir)
    {
        return EnumerateSorted(dialogueDir);
    }

    private static List<string> EnumerateSorted(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return new List<string>();
        }
        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(IsTranslationFile)
            .OrderBy(f => RelativeSortKey(dir, f), StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsTranslationFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".json", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".jsonc", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>排序键：统一为 '/' 分隔的相对路径（Windows 的 '\' 归一化，Linux 本就为 '/'）。</summary>
    private static string RelativeSortKey(string baseDir, string file) =>
        Path.GetRelativePath(baseDir, file).Replace('\\', '/');

    /// <summary>相对路径的任一级目录名为 dialogue 即视为 dialogue 文件（与原 Contains("\dialogue\") 行为一致）。</summary>
    private static bool IsUnderDialogueDir(string relativePath) =>
        relativePath.Split('/').Any(c => c.Equals("dialogue", StringComparison.OrdinalIgnoreCase));
}
