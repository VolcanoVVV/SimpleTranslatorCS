using SPTarkov.Server.Core.Models.Eft.Common.Tables;
#if SPT50
using DialogueId = SPTarkov.Server.Core.Models.Common.MongoId;
#else
using DialogueId = System.String;
#endif

namespace SimpleTranslatorCS;

/// <summary>
/// Dialogue 本地化：把 dialogue 目录下合并后的词条写入对话数据表。
/// 只更新对话元素中已存在的文本 ID（不新增键），目标语言槽位不存在时自动创建。
/// 性能说明：先把所有元素出现过的键建成并集做预过滤，再把每个元素的键建成集合，
/// 避免 元素数 x 词条数 x 语言数 的三重循环。
/// </summary>
internal static class DialogueLocalizer
{
    /// <param name="dialogue">对话数据表（4.1 取自 TemplateTable，4.0 取自 DatabaseServer.GetTables().Templates）</param>
    /// <param name="entries">合并后的词条：键 -> (文本, 来源文件)</param>
    /// <param name="targetLocaleRef">目标语言当前字典（占位符解析首选），可为 null</param>
    /// <param name="enLocaleRef">英文字典（占位符解析兜底），可为 null</param>
    public static void Apply(
        TraderDialogs? dialogue,
        string locale,
        Dictionary<string, (string value, string file)> entries,
        IReadOnlyDictionary<string, string>? targetLocaleRef,
        IReadOnlyDictionary<string, string>? enLocaleRef,
        TranslationReport? report,
        Action<string> info,
        Action<string> warn)
    {
        if (entries.Count == 0)
        {
            return;
        }
        if (dialogue?.Elements == null)
        {
            warn("[文本汉化][对话]无法获取 dialogue 数据");
            return;
        }

        // 预建所有元素出现过的键的并集：不在并集里的词条直接判未生效，
        // 无需进入元素循环
        var allKnownKeys = new HashSet<string>();
        foreach (var element in dialogue.Elements)
        {
            if (element.LocalizationDictionary == null)
            {
                continue;
            }
            foreach (var dict in element.LocalizationDictionary.Values)
            {
                allKnownKeys.UnionWith(dict.Keys.Select(key => key.ToString()));
            }
        }

        var updatedDialogueCount = 0;
        var updatedTextCount = 0;

        // 遍历所有 dialogue elements，只更新目标语言的文本
        foreach (var element in dialogue.Elements)
        {
            if (element.LocalizationDictionary == null)
            {
                element.LocalizationDictionary = new Dictionary<string, Dictionary<DialogueId, string>>();
            }

            // 本元素出现过的键（任意语言），建成集合避免内层再遍历语言
            var elementKeys = new HashSet<string>();
            foreach (var dict in element.LocalizationDictionary.Values)
            {
                elementKeys.UnionWith(dict.Keys.Select(key => key.ToString()));
            }

            if (!element.LocalizationDictionary.TryGetValue(locale, out var localeDict))
            {
                localeDict = new Dictionary<DialogueId, string>();
                element.LocalizationDictionary[locale] = localeDict;
            }

            var textsUpdated = 0;
            foreach (var kvp in entries)
            {
                // 文本 ID 已存在于该元素的任意语言中，才更新目标语言的翻译
                if (!elementKeys.Contains(kvp.Key))
                {
                    continue;
                }

                var unresolved = report != null ? new List<string>() : null;
                localeDict[(DialogueId)kvp.Key] = ItemPlaceholders.Resolve(kvp.Value.value, targetLocaleRef, enLocaleRef, unresolved);
                if (unresolved != null)
                {
                    foreach (var id in unresolved)
                    {
                        report!.AddUnresolvedPlaceholder(kvp.Value.file, kvp.Key, id);
                    }
                }
                textsUpdated++;
            }

            if (textsUpdated > 0)
            {
                updatedDialogueCount++;
                updatedTextCount += textsUpdated;
            }
        }

        if (updatedDialogueCount > 0)
        {
            info($"文本汉化 | 对话已写入 {locale} 语言槽位：更新 {updatedDialogueCount} 个对话元素，共写入 {updatedTextCount} 条文本。");
        }

        // 开发模式：记录每条 dialogue 词条是否匹配到对话元素——
        // 匹配到即已写入对应语言槽位；未匹配仅代表未写入，不代表游戏中不存在该对话
        if (report != null)
        {
            foreach (var kvp in entries)
            {
                report.AddDialogue(kvp.Value.file, kvp.Key, kvp.Value.value, allKnownKeys.Contains(kvp.Key));
            }
        }
    }
}
