using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace SimpleTranslatorCS;

/// <summary>
/// 开发模式报告：收集每条词条的生效状态、跨文件键冲突、占位符解析失败，
/// 最后导出为带注释的 jsonc。
/// 生效判定——普通本地化：键存在于 en 或目标语言现有字典（覆盖到了游戏原文）；
/// dialogue：键匹配到了至少一个对话元素。
/// 不依赖任何 SPT 类型，日志通过回调外抛，便于单元测试。
/// </summary>
internal sealed class TranslationReport
{
    // 保留中文原文（不转义为 \uXXXX），否则报告在记事本里显示为转义序列
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private readonly List<(string file, string key, string value, bool covered)> _localeEntries = new();
    private readonly List<(string file, string key, string value, bool covered)> _dialogueEntries = new();
    private readonly List<(string key, string firstFile, string overrideFile)> _conflicts = new();
    private readonly List<(string file, string key, string itemId)> _unresolvedPlaceholders = new();

    public void AddLocale(string file, string key, string value, bool covered)
        => _localeEntries.Add((file, key, value, covered));

    public void AddDialogue(string file, string key, string value, bool covered)
        => _dialogueEntries.Add((file, key, value, covered));

    /// <summary>记录跨文件键冲突：同一个键被多个文件翻译，后加载的文件覆盖先加载的</summary>
    public void AddConflict(string key, string firstFile, string overrideFile)
        => _conflicts.Add((key, firstFile, overrideFile));

    /// <summary>记录解析不到物品名的占位符</summary>
    public void AddUnresolvedPlaceholder(string file, string key, string itemId)
        => _unresolvedPlaceholders.Add((file, key, itemId));

    public void WriteTo(string path, Action<string> info, Action<string> error)
    {
        try
        {
            var sb = new StringBuilder();
            var totalCount = _localeEntries.Count + _dialogueEntries.Count;
            var coveredCount = _localeEntries.Count(e => e.covered) + _dialogueEntries.Count(e => e.covered);

            sb.AppendLine("{");
            sb.AppendLine(" /*");
            sb.AppendLine(" ** [汉化报告] SimpleTranslatorCS 词条生效报告（开发模式生成）");
            sb.AppendLine($" ** [生成时间] {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($" ** [统计] 生效 {coveredCount} / 未生效 {totalCount - coveredCount} / 总计 {totalCount}"
                + $" / 键冲突 {_conflicts.Count} / 占位符解析失败 {_unresolvedPlaceholders.Count}");
            sb.AppendLine(" ** [说明] 每条词条上方的注释标记其状态：");
            sb.AppendLine(" **   [生效]   普通本地化 = 键在英文或中文现有字典中存在（覆盖到了游戏原文）；dialogue = 匹配到了对话元素");
            sb.AppendLine(" **   [未生效] 键未被游戏数据使用，这条翻译不会显示，请检查键名或对应 mod 是否安装");
            sb.AppendLine(" */");

            AppendEntrySection(sb, "普通本地化", _localeEntries);
            AppendEntrySection(sb, "Dialogue 对话", _dialogueEntries);

            if (_conflicts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("    /* ========== 键冲突（同一个键被多个文件翻译，后者覆盖前者） ========== */");
                foreach (var c in _conflicts)
                {
                    sb.AppendLine($"    // \"{c.key}\": {c.firstFile}  <-- 被覆盖 <--  {c.overrideFile}");
                }
                sb.AppendLine("    /* 冲突明细见上方注释，共 " + _conflicts.Count + " 条 */");
            }

            if (_unresolvedPlaceholders.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("    /* ========== 占位符解析失败（找不到物品名，游戏里显示为裸 id） ========== */");
                foreach (var u in _unresolvedPlaceholders)
                {
                    sb.AppendLine($"    // [文件] {u.file}  [键] {u.key}  [物品id] {u.itemId} —— 物品不存在或对应 mod 未安装");
                }
                sb.AppendLine("    /* 失败明细见上方注释，共 " + _unresolvedPlaceholders.Count + " 条 */");
            }

            sb.AppendLine("}");
            // 带 BOM 的 UTF-8：让记事本等编辑器正确识别编码，避免中文按 ANSI 显示为乱码
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            info($"[文本汉化]词条生效报告已导出: {path}（生效 {coveredCount}/{totalCount}）");
        }
        catch (Exception ex)
        {
            error($"[文本汉化]导出词条报告失败: {ex.Message}");
        }
    }

    private static void AppendEntrySection(
        StringBuilder sb,
        string sectionName,
        List<(string file, string key, string value, bool covered)> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine($"    /* ========== {sectionName} ========== */");

        foreach (var group in entries.GroupBy(e => e.file))
        {
            var covered = group.Count(e => e.covered);
            sb.AppendLine();
            sb.AppendLine($"    /* [文件] {group.Key} (生效 {covered}/{group.Count()}) */");
            foreach (var e in group)
            {
                sb.AppendLine(e.covered ? "    // [生效]" : "    // [未生效]");
                // JsonSerializer.Serialize 负责引号包裹与转义，保证报告本身是合法 jsonc
                sb.AppendLine($"    {JsonSerializer.Serialize(e.key, JsonOptions)}: {JsonSerializer.Serialize(e.value, JsonOptions)},");
            }
        }
    }
}
