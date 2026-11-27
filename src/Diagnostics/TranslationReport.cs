using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace SimpleTranslatorCS;

/// <summary>
/// 开发模式报告：收集每条词条的状态、跨文件键冲突、占位符解析失败，
/// 最后导出为带注释的 jsonc。
/// 普通本地化条目记录基底是否存在、目标语言是否可用、本 mod 内合并胜出与来源。
/// Global 报告表示已注册覆盖，实际写入发生在 locale 构建时，不能证明最终 UI 生效；
/// 基底不存在的键只标注"新增键，客户端是否引用未知"，不断言其不会显示。
/// dialogue 条目记录"键是否匹配到至少一个对话元素"（匹配到 = 已写入对应语言槽位）。
/// 不依赖任何 SPT 类型，日志通过回调外抛，便于单元测试。
/// </summary>
internal sealed class TranslationReport
{
    // 保留中文原文（不转义为 \uXXXX），否则报告在记事本里显示为转义序列
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>普通本地化条目：baseExists = 键在基底中存在；isWinner = 本条为同键最终生效值；败方时 winnerFile 指向胜出文件</summary>
    private sealed record LocaleEntry(string File, string Key, string Value, bool BaseExists, bool IsWinner, string? WinnerFile, bool TargetAvailable);

    private readonly List<LocaleEntry> _localeEntries = new();
    private readonly List<(string file, string key, string value, bool matched)> _dialogueEntries = new();
    private readonly List<(string key, string firstFile, string overrideFile)> _conflicts = new();
    private readonly List<(string file, string key, string itemId)> _unresolvedPlaceholders = new();

    public void AddLocale(string file, string key, string value, bool baseExists, bool isWinner, string? winnerFile, bool targetAvailable = true)
        => _localeEntries.Add(new LocaleEntry(file, key, value, baseExists, isWinner, winnerFile, targetAvailable));

    public void AddDialogue(string file, string key, string value, bool matched)
        => _dialogueEntries.Add((file, key, value, matched));

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
            var winners = _localeEntries.Count(e => e.IsWinner);
            var winnersBase = _localeEntries.Count(e => e.IsWinner && e.BaseExists);
            var winnersNew = winners - winnersBase;
            var losers = _localeEntries.Count - winners;
            var losersBase = _localeEntries.Count(e => !e.IsWinner && e.BaseExists);
            var losersNew = losers - losersBase;
            var dialogueMatched = _dialogueEntries.Count(e => e.matched);

            sb.AppendLine("{");
            sb.AppendLine(" /*");
            sb.AppendLine(" ** [汉化报告] SimpleTranslatorCS 词条生效报告（开发模式生成）");
            sb.AppendLine($" ** [生成时间] {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($" ** [统计] 普通本地化：胜出 {winners}（覆盖基底 {winnersBase} / 新增键 {winnersNew}），"
                + $"被同键覆盖 {losers}（基底存在 {losersBase} / 基底不存在 {losersNew}），总计 {_localeEntries.Count}");
            sb.AppendLine($" **        Dialogue：已写入 {dialogueMatched} / 未匹配 {_dialogueEntries.Count - dialogueMatched}，总计 {_dialogueEntries.Count}"
                + $"；键冲突 {_conflicts.Count} / 占位符解析失败 {_unresolvedPlaceholders.Count}");
            sb.AppendLine(" ** [说明] 每条词条上方的注释标记其状态：");
            sb.AppendLine(" **   普通本地化（判定基于启动时的基底快照与本 mod 的合并结果；基底 = en 或目标语言现有字典）：");
            sb.AppendLine(" **     [胜出]   本条为本 mod 合并结果的胜出值；已注册覆盖，实际写入在 locale 构建时发生。");
            sb.AppendLine(" **              \"新增键\" = 基底中不存在该键，客户端是否引用该键未知");
            sb.AppendLine(" **     [被覆盖] 同键被其他文件覆盖，本条不是最终生效值，最终生效值来自标注的胜出文件");
            sb.AppendLine(" **   [未注册] 目标语言不存在，未注册覆盖；合并胜出不代表已经应用。");
            sb.AppendLine(" **   加载顺序在本 mod 之后的其他 mod 仍可能覆盖最终结果，本报告不含其影响");
            sb.AppendLine(" **   Dialogue 对话：");
            sb.AppendLine(" **     [已写入] 键匹配到至少一个对话元素，已写入对应语言槽位");
            sb.AppendLine(" **     [未匹配] 未匹配到任何对话元素，未写入（不代表游戏中不存在该对话，可能键名不符或对应 mod 未安装）");
            sb.AppendLine(" */");

            AppendLocaleSection(sb);
            AppendDialogueSection(sb);

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
                sb.AppendLine("    /* ========== 占位符解析失败（缺失、循环或超限；回退裸 id） ========== */");
                foreach (var u in _unresolvedPlaceholders)
                {
                    sb.AppendLine($"    // [文件] {u.file}  [键] {u.key}  [物品id] {u.itemId} —— 缺失、循环引用或解析预算超限");
                }
                sb.AppendLine("    /* 失败明细见上方注释，共 " + _unresolvedPlaceholders.Count + " 条 */");
            }

            sb.AppendLine("}");
            // 带 BOM 的 UTF-8：让记事本等编辑器正确识别编码，避免中文按 ANSI 显示为乱码
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            info($"[文本汉化]词条生效报告已导出: {path}（普通本地化胜出 {winners}/{_localeEntries.Count}）");
        }
        catch (Exception ex)
        {
            error($"[文本汉化]导出词条报告失败。{ServerLogText.Failure(ex)}");
        }
    }

    private void AppendLocaleSection(StringBuilder sb)
    {
        if (_localeEntries.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("    /* ========== 普通本地化 ========== */");

        foreach (var group in _localeEntries.GroupBy(e => e.File))
        {
            var winners = group.Count(e => e.IsWinner);
            var winnersBase = group.Count(e => e.IsWinner && e.BaseExists);
            sb.AppendLine();
            sb.AppendLine($"    /* [文件] {group.Key}（胜出 {winners}/{group.Count()}：覆盖基底 {winnersBase}，新增键 {winners - winnersBase}） */");
            foreach (var e in group)
            {
                if (!e.TargetAvailable)
                {
                    sb.AppendLine("    // [未注册] 目标语言不存在；未写入，无论本条是否在合并结果中胜出");
                }
                else if (e.IsWinner)
                {
                    sb.AppendLine(e.BaseExists
                        ? "    // [胜出] 覆盖基底词条，已注册，读取 locale 时应用"
                        : "    // [胜出] 新增键（基底不存在，已注册；客户端是否引用该键未知）");
                }
                else
                {
                    sb.AppendLine($"    // [被覆盖] 同键最终生效值来自: {e.WinnerFile}");
                }
                // JsonSerializer.Serialize 负责引号包裹与转义，保证报告本身是合法 jsonc
                sb.AppendLine($"    {JsonSerializer.Serialize(e.Key, JsonOptions)}: {JsonSerializer.Serialize(e.Value, JsonOptions)},");
            }
        }
    }

    private void AppendDialogueSection(StringBuilder sb)
    {
        if (_dialogueEntries.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("    /* ========== Dialogue 对话 ========== */");

        foreach (var group in _dialogueEntries.GroupBy(e => e.file))
        {
            var matched = group.Count(e => e.matched);
            sb.AppendLine();
            sb.AppendLine($"    /* [文件] {group.Key} (已写入 {matched}/{group.Count()}) */");
            foreach (var e in group)
            {
                sb.AppendLine(e.matched ? "    // [已写入]" : "    // [未匹配]");
                sb.AppendLine($"    {JsonSerializer.Serialize(e.key, JsonOptions)}: {JsonSerializer.Serialize(e.value, JsonOptions)},");
            }
        }
    }
}
