using System.Collections;
using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
using AchievementTable = SPTarkov.Server.Core.Models.Eft.Common.Tables.Achievement;
using QuestTable = SPTarkov.Server.Core.Models.Eft.Common.Tables.Quest;
#if SPT40
// SPT 4.0 的 API 布局（dotnet build -c "4.0 Release"）
using SPTarkov.Server.Core.Helpers;        // ModHelper
using SPTarkov.Server.Core.Models.Logging; // LogBackgroundColor
using SPTarkov.Server.Core.Models.Utils;   // ISptLogger<T>
using SPTarkov.Server.Core.Servers;        // DatabaseServer
using SPTarkov.Server.Core.Services;       // LocaleService
// 4.0 的日志颜色是自己的枚举，起别名让调用处与 4.1 的 Spectre.Console.Color 写法一致
using Color = SPTarkov.Server.Core.Models.Logging.LogTextColor;
#else
// SPT 4.1 的 API 布局（默认 dotnet build）
using SPTarkov.Common.Models.Logging;         // ISptLogger<T>
using SPTarkov.Server.Core.Helpers.Server;    // ModHelper
using SPTarkov.Server.Core.Models.Spt.Tables; // LocaleTable / TemplateTable / GlobalLocaleDictionary
using SPTarkov.Server.Core.Services.Locales;  // LocaleService
using Spectre.Console;                        // Color
#endif

namespace SimpleTranslatorCS;

// 版本号单一来源是 csproj 的 <Version>，这里从程序集元数据读回，发版只需改 csproj
internal static class ModInfo
{
    public static readonly string Version =
        typeof(ModInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "1.11.0";
}

#if SPT40
// SPT 4.0：元数据是 record 继承 AbstractModMetadata（属性全为 abstract，必须 override；
// 4.0 没有 HasPrepatcher，多一个 IsBundleMod）
public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.simple.translator.cs";
    public override string Name { get; init; } = "SimpleTranslatorCS";
    public override string Author { get; init; } = "MiKueYu & Volcano";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } = new(ModInfo.Version);
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.0");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; }
    public override string License { get; init; } = "MIT";
}
#else
// SPT 4.1：元数据是 record 实现 IModMetadata
public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.simple.translator.cs";
    public string Name { get; init; } = "SimpleTranslatorCS";
    public string Author { get; init; } = "MiKueYu & Volcano";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new(ModInfo.Version);
#if SPT50
    // 5.0 BE reports a prerelease such as 5.0.0-BLEEDINGEDGEMODS; ~5.0.0 excludes it.
    public SemanticVersioning.Range SptVersion { get; init; } = new(">=5.0.0-0 <5.0.0");
#else
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
#endif
    public bool HasPrepatcher { get; init; } = false;
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
}
#endif

#if SPT40
// 4.0 没有 LocaleTable/TemplateTable 注入，通过 DatabaseServer.GetTables() 取表。
// 加载顺序：大多数 mod 在 PostDBModLoader 档执行，翻译 mod 必须排在它们之后，
// 否则被翻译 mod 的数据还没写入（dialogue 直写会错过、transformer 顺序也吃亏）。
// PostSptModLoader 是 4.0 最后一档，再 +1 保证排在同档其他 mod 之后。
[Injectable(TypePriority = OnLoadOrder.PostSptModLoader + 1)]
public class SimpleTranslator(
    ISptLogger<SimpleTranslator> logger,
    ModHelper modHelper,
    LocaleService localeService,
    DatabaseServer databaseServer) : IOnLoad
#else
// 加载顺序：4.1 里所有 mod 默认都在 PostLoad 档执行，同档内按注册顺序排列。
// 翻译 mod 必须最后执行，+1 保证排在 PostLoad 档所有 mod 之后，
// 否则被翻译 mod 的数据还没写入（dialogue 直写会错过、transformer 顺序也吃亏）。
[Injectable(TypePriority = OnLoadOrder.PostLoad + 1)]
public class SimpleTranslator(
    ISptLogger<SimpleTranslator> logger,
    ModHelper modHelper,
    LocaleService localeService,
    LocaleTable localeTable,
    TemplateTable templateTable,
    TradersTable tradersTable) : IOnLoad
#endif
{
    // 目标语言（本 mod 只服务中文用户）
    private static readonly string[] ServerLocales = { "ch" };

    // devMode 文件监听器需要一直存活，用静态字段持有防止被 GC
    private static readonly Dictionary<string, FileSystemWatcher> FileWatchers = new(StringComparer.OrdinalIgnoreCase);

#if SPT40
    // 4.0 的 IOnLoad 是 OnLoad()，4.1 是 OnLoadAsync(CancellationToken)
    public Task OnLoad()
#else
    public Task OnLoadAsync(CancellationToken cancellationToken)
#endif
    {
        var loadedFileCount = 0;

        var fileCoverage = new List<FileCoverage>();

        // 语言级写入统计：(写入键数, 覆盖基底, 新增键)
        var writtenLocalesByLang = new Dictionary<string, (int Written, int Covered, int Added)>();

        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var fikaDetected = WorkbenchAccess.DetectFika(modPath);

        // 开发/日志模式：mod 目录下 config.jsonc 中 devMode=true 时，
        // 加载完成后导出词条生效报告，并监听翻译文件变更给出重启提示
        var config = ModConfig.Load(modPath, m => logger.Warning($"[文本汉化]{m}"));
        // Administrator protection applies to every server, including installations without Fika.
        {
            try
            {
                var key = WorkbenchAccess.EnsureKey(modPath, config.WorkbenchAdminKey);
                config = config with { WorkbenchAdminKey = key };
                logger.Info($"[文本汉化]管理员写入保护已启用。密钥文件：{WorkbenchAccess.KeyFileName}。普通玩家手动拉取冷却：{config.PullCooldownSeconds} 秒。");
            }
            catch (Exception ex)
            {
                config = config with { WorkbenchAdminKey = string.Empty };
                logger.Error($"[文本汉化]无法使用管理员密钥，工作台写操作已禁用。请检查 {WorkbenchAccess.KeyFileName}。{ServerLogText.Failure(ex)}");
            }
        }
        try
        {
            TranslationWorkbench.PrepareStorage(modPath);
        }
        catch (Exception ex)
        {
            logger.Warning($"[文本汉化]旧工作台文件归类失败，将继续加载现有文件。{ServerLogText.Failure(ex)}");
        }
        var report = config.DevMode ? new TranslationReport() : null;
        if (config.DevMode)
        {
            logger.Info("[文本汉化]开发模式已启用，加载完成后将导出词条生效报告");
        }

        foreach (var locale in ServerLocales)
        {
            var localePath = TranslationWorkbench.LocaleRoot(modPath, locale);
            if (!TranslationWorkbench.LocaleRoots(modPath, locale).Any(Directory.Exists))
            {
                logger.Warning($"[文本汉化]语言 {locale} 没有可读取的译文目录。请在当前 Mod 目录下检查 translations/{locale} 或旧 db/locales/{locale}。");
                continue;
            }

            if (config.DevMode)
            {
                foreach (var folder in TranslationWorkbench.LocaleRoots(modPath, locale).Where(Directory.Exists))
                    StartFileWatcher(folder, logger);
            }

            // 生效判定的参考字典：英文为主，目标语言现有字典兜底
            // （有些键只存在于官方中文或其他 mod 已写入的中文里，单看 en 会误报未生效）
            var referenceLocales = localeService.GetLocaleDb("en");
            IReadOnlyDictionary<string, string>? targetLocaleRef = null;
            try
            {
                targetLocaleRef = localeService.GetLocaleDb(locale);
            }
            catch
            {
                // 目标语言字典不可用时仅用 en 判定
            }

            // 枚举 + 解析全部有效翻译文件（稳定排序即优先级：同键后文件覆盖前文件）
            var parsedFiles = new List<(string File, IReadOnlyDictionary<string, string> Entries)>();
            var filterTracked = TranslationWorkbench.StartupFilter(modPath, referenceLocales);
            foreach (var file in TranslationWorkbench.OrderForLoading(localePath,
                         TranslationWorkbench.EnumerateLocaleFiles(modPath, locale))
                         .Where(file => TranslationWorkbench.IsManualFileEnabled(modPath, localePath, file)))
            {
                loadedFileCount++;

                var content = Jsonc.ParseFile(file, m => logger.Warning($"[文本汉化]{m}"), TranslationFiles.KeyComparer,
                    m => logger.Error($"[文本汉化]{m}"));
                if (content == null || content.Count == 0)
                {
                    continue;
                }
                try
                {
                    var relative = TranslationWorkbench.NormalizeRelative(localePath, file);
                    var active = filterTracked(file, content);
                    if (active.Count < content.Count)
                        logger.Warning($"[文本汉化]译文文件 {relative} 有 {content.Count - active.Count} 条因原文基准变化而暂停应用，请校对后重新保存。");
                    parsedFiles.Add((relative, active));
                }
                catch (Exception ex) { logger.Warning($"[文本汉化]已暂停无法校验原文基准的生成文件：{Path.GetFileName(file)}。{ServerLogText.Failure(ex)}"); }
            }

            // 全量合并（含每个键的覆盖链 provenance，供冲突报告与占位符归属使用）
            var merged = TranslationMerger.Merge(parsedFiles);

            // 目标语言容器必须存在。不存在时保持旧语义：词条仍记入报告，但不计入统计、不注册 transformer
#if SPT40
            var localeGlobal = databaseServer.GetTables().Locales.Global;
#else
            var localeGlobal = localeTable.Global;
#endif
            var targetAvailable = localeGlobal.ContainsKey(locale);

            // 逐文件统计并写入报告。生效语义（与报告一致）：
            //   覆盖 = 胜出且基底存在；新增 = 胜出但基底不存在；被同键其他文件覆盖的败方两者都不算
            foreach (var (relativeFile, content) in parsedFiles)
            {
                var totalFields = content.Count;
                var coveredFields = 0;
                var newKeyFields = 0;
                foreach (var kvp in content)
                {
                    // 基底 = 英文或目标语言现有字典；胜出 = 本文件是同键最终生效值的来源文件
                    var baseExists = referenceLocales.ContainsKey(kvp.Key)
                                     || (targetLocaleRef?.ContainsKey(kvp.Key) ?? false);
                    var winnerFile = merged.WinningFile(kvp.Key);
                    var isWinner = winnerFile == relativeFile;
                    if (isWinner && baseExists)
                    {
                        coveredFields++;
                    }
                    else if (isWinner)
                    {
                        newKeyFields++;
                    }
                    report?.AddLocale(relativeFile, kvp.Key, kvp.Value, baseExists, isWinner, winnerFile, targetAvailable);
                }

                if (!targetAvailable)
                {
                    continue;
                }

                if (totalFields > 0)
                    fileCoverage.Add(new FileCoverage(relativeFile, coveredFields, newKeyFields, totalFields));
            }

            // 语言级写入统计：进入合并结果的每个键都会写入目标语言字典，
            // 其中基底存在的为覆盖基底，基底不存在的为新增键
            if (targetAvailable && merged.Values.Count > 0)
            {
                var baseCovered = merged.Values.Keys.Count(k =>
                    referenceLocales.ContainsKey(k) || (targetLocaleRef?.ContainsKey(k) ?? false));
                writtenLocalesByLang[locale] = (merged.Values.Count, baseCovered, merged.Values.Count - baseCovered);
            }

            // 冲突报告：覆盖链上每一对相邻覆盖记一条（后者覆盖前者）
            foreach (var (key, chain) in merged.OverrideChains)
            {
                for (var i = 1; i < chain.Count; i++)
                {
                    report?.AddConflict(key, chain[i - 1], chain[i]);
                }
            }



            if (!targetAvailable)
            {
                logger.Warning($"[文本汉化]数据库缺少目标语言 {locale}，已跳过该语言的译文。");
                continue;
            }
            var lazyTarget = localeGlobal[locale];

            // 开发模式：基于合并后的完整字典预览占位符解析结果（跨文件引用可见，比逐文件预览更准），
            // 缺失归属到该键的胜出文件；最终以运行时 transformer 的警告日志为准，mod 物品的预览可能误报
            if (report != null)
            {
                var preview = new Dictionary<string, string>(referenceLocales, TranslationFiles.KeyComparer);
                if (targetLocaleRef != null)
                {
                    foreach (var p in targetLocaleRef)
                    {
                        preview[p.Key] = p.Value;
                    }
                }
                foreach (var p in merged.Values)
                {
                    preview[p.Key] = p.Value;
                }
                foreach (var kvp in merged.Values)
                {
                    var unresolved = new List<string>();
                    ItemPlaceholders.Resolve(kvp.Value, preview, referenceLocales, unresolved);
                    foreach (var id in unresolved)
                    {
                        report.AddUnresolvedPlaceholder(merged.WinningFile(kvp.Key)!, kvp.Key, id);
                    }
                }
            }

            // 每语言只注册一个 transformer。
            // transformer 在 locale 字典真正构建时才执行，此时其他 mod
            // 添加的物品名已进入字典，mod 物品也能正确解析
            var initialApplyLogged = 0;
            lazyTarget.AddTransformer(targetDict =>
            {
                // Discovery travels on the built-in locale route. A client without this mod
                // never calls an optional /simpletranslator endpoint.
#if SPT40
                // 4.0 的 Global 字典就是普通的 Dictionary<string, string>
                targetDict ??= new Dictionary<string, string>();
#else
                targetDict ??= new GlobalLocaleDictionary();
#endif

                if (locale == "ch") TranslationWorkbench.CaptureBaseTranslations(targetDict, merged.Values.Keys);

                TranslationMerger.ApplyTo(targetDict, merged, referenceLocales, (key, id) =>
                    logger.Warning($"[文本汉化]占位符未解析：{id}（{merged.WinningFile(key)}，键 {key}）。物品可能缺失、循环引用或超出解析预算，已保留原始 ID。"));
                targetDict[SimpleTranslatorRouter.DiscoveryKey] = "SimpleTranslatorCS:2";
                if (locale == "ch") ServerLocaleOverlay.Apply(targetDict);
                if (merged.Values.Count > 0 && Interlocked.Exchange(ref initialApplyLogged, 1) == 0)
                    LogColored(logger, $"文本汉化 | 已写入 {locale} 语言表：{merged.Values.Count} 个文件合并键。", Color.Green);
                return targetDict;
            });
        }

        LogStatistics(loadedFileCount, writtenLocalesByLang, fileCoverage, logger);

        // 处理 dialogue 本地化
        LoadDialogueLocalizations(modPath, report);

        try
        {
            var english = localeService.GetLocaleDb("en");
            var chinese = localeService.GetLocaleDb("ch");
#if SPT40
            var templates = databaseServer.GetTables().Templates;
            var traderIds = databaseServer.GetTables().Traders.Keys.Select(id => id.ToString());
#else
            var templates = templateTable;
            var traderIds = tradersTable.Keys.Select(id => id.ToString());
#endif
            TranslationWorkbench.Initialize(
                modPath,
                english,
                chinese,
                templates.Items.Keys.Select(id => id.ToString()),
                templates.Quests.Values.SelectMany(QuestLocaleKeys),
                fikaDetected: fikaDetected,
                adminKey: config.WorkbenchAdminKey,
                pullCooldownSeconds: config.PullCooldownSeconds,
                achievementKeys: templates.Achievements.Concat(templates.CustomAchievements ?? []).SelectMany(AchievementLocaleKeys),
                traderIds: traderIds,
                customizationIds: templates.Customization.Keys.Select(id => id.ToString()),
                handbookKeys: templates.Handbook.Categories.Select(category => category.Id.ToString()),
                includeOtherLocaleText: config.IncludeOtherLocaleText);
        }
        catch (Exception ex)
        {
            logger.Warning($"[文本汉化]工作台目录初始化失败。{ServerLogText.Failure(ex)}");
        }

        // 开发模式：导出词条生效报告
        report?.WriteTo(System.IO.Path.Combine(modPath, config.ReportFile), m => logger.Info(m), m => logger.Error(m));

        return Task.CompletedTask;
    }

    private static IEnumerable<string> QuestLocaleKeys(QuestTable quest)
    {
        yield return quest.Id.ToString();
        foreach (var value in new[]
                 {
                     quest.Name, quest.Description, quest.FailMessageText, quest.Note, quest.Location,
                     quest.StartedMessageText, quest.SuccessMessageText, quest.AcceptPlayerMessage,
                     quest.AcceptanceAndFinishingSource, quest.DeclinePlayerMessage, quest.CompletePlayerMessage,
                     quest.ChangeQuestMessageText
                 })
            if (!string.IsNullOrWhiteSpace(value))
                yield return value;

        foreach (var key in ConditionLocaleKeys(quest.Conditions))
            yield return key;
    }

    private static IEnumerable<string> AchievementLocaleKeys(AchievementTable achievement)
    {
        var id = achievement.Id.ToString();
        if (string.IsNullOrWhiteSpace(id))
            yield break;
        yield return id + " name";
        yield return id + " description";
        yield return id + " successMessage";
        foreach (var key in ConditionLocaleKeys(achievement.Conditions))
            yield return key;
    }

    private static readonly HashSet<string> ConditionChildProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "Started", "AvailableForFinish", "AvailableForStart", "Success", "Fail",
        "Counter", "Conditions", "VisibilityConditions"
    };

    /// <summary>
    /// Collect locale-capable IDs from quest/achievement condition trees. Traversal is restricted
    /// to condition-container properties so reward, item and trader IDs elsewhere cannot leak into
    /// the automatic translation catalog.
    /// </summary>
    internal static IReadOnlyCollection<string> ConditionLocaleKeys(object? root)
    {
        const int maxDepth = 12;
        const int maxNodes = 50000;
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodes = 0;

        void Visit(object? value, int depth)
        {
            if (value == null || depth > maxDepth || nodes++ >= maxNodes || value is string)
                return;
            if (value is IEnumerable sequence)
            {
                foreach (var item in sequence)
                {
                    Visit(item, depth + 1);
                    if (nodes >= maxNodes)
                        break;
                }
                return;
            }

            var type = value.GetType();
            var idProperty = type.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (idProperty?.GetIndexParameters().Length == 0)
            {
                try
                {
                    var id = idProperty.GetValue(value)?.ToString();
                    if (!string.IsNullOrWhiteSpace(id))
                        result.Add(id);
                }
                catch
                {
                    // A malformed third-party condition must not prevent the remaining catalog.
                }
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(property => ConditionChildProperties.Contains(property.Name)
                                            && property.GetIndexParameters().Length == 0))
            {
                try
                {
                    Visit(property.GetValue(value), depth + 1);
                }
                catch
                {
                    // Skip an unreadable extension property and continue with sibling conditions.
                }
            }
        }

        Visit(root, 0);
        return result;
    }

    /// <summary>
    /// 开发模式：监听翻译目录的文件变更，提示译者修改需重启服务器生效。
    /// 当前尚未实现快照替换与缓存能力适配，所以这里只做提示；不是 SPT 不支持重载。
    /// </summary>
    private static void StartFileWatcher(string localePath, ISptLogger<SimpleTranslator> logger)
    {
        if (FileWatchers.ContainsKey(Path.GetFullPath(localePath)))
        {
            return; // 已启动
        }

        try
        {
            var watcher = new FileSystemWatcher(localePath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            var lastNotified = new Dictionary<string, DateTime>();

            void OnChanged(object _, FileSystemEventArgs e)
            {
                var ext = System.IO.Path.GetExtension(e.FullPath).ToLowerInvariant();
                if (ext is not (".json" or ".jsonc"))
                {
                    return;
                }
                // 去抖：编辑器一次保存常触发多个事件，500ms 内同一路径只提示一次
                if (lastNotified.TryGetValue(e.FullPath, out var t) && (DateTime.Now - t).TotalMilliseconds < 500)
                {
                    return;
                }
                lastNotified[e.FullPath] = DateTime.Now;
                logger.Info($"[文本汉化]检测到译文文件变更：{e.FullPath}。可用管理员工作台重载普通译文；对话（dialogue）变更需重启服务器。");
            }

            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnChanged;

            FileWatchers[Path.GetFullPath(localePath)] = watcher;
        }
        catch (Exception ex)
        {
            logger.Warning($"[文本汉化]译文文件监听启动失败。{ServerLogText.Failure(ex)}");
        }
    }

    /// <summary>
    /// 彩色日志包装：4.1 用 Spectre.Console.Color（前景+背景），
    /// 4.0 用自带的 LogTextColor/LogBackgroundColor 枚举，调用处统一传 Color 前景色即可
    /// </summary>
    private static void LogColored(ISptLogger<SimpleTranslator> logger, string message, Color color)
    {
#if SPT40
        logger.LogWithColor(message, color, LogBackgroundColor.Black);
#else
        logger.LogWithColor(message, color, Color.Black);
#endif
    }

    private sealed record FileCoverage(string Path, int Covered, int Added, int Total)
    {
        internal int Overridden => Total - Covered - Added;
    }

    private static void LogStatistics(
        int scannedFiles,
        Dictionary<string, (int Written, int Covered, int Added)> byLanguage,
        IReadOnlyList<FileCoverage> files,
        ISptLogger<SimpleTranslator> logger)
    {
        logger.Info($"[文本汉化]已尝试读取 {scannedFiles} 个启用的普通译文文件（不含对话）。");
        foreach (var locale in ServerLocales)
        {
            if (!byLanguage.TryGetValue(locale, out var counts))
            {
                logger.Info($"[文本汉化]语言 {locale} 没有注册可写入的普通译文键。");
                continue;
            }

            logger.Info($"[文本汉化]语言 {locale} 已注册 {counts.Written} 个合并键，读取语言表时写入。");
            if (counts.Covered > 0)
                LogColored(logger, $"文本汉化 | 覆盖已有键：{counts.Covered} 个。", Color.Green);
            if (counts.Added > 0)
                LogColored(logger, $"文本汉化 | 新增键：{counts.Added} 个。若游戏没有引用这些键，界面可能不会显示。", Color.Cyan);
        }

        foreach (var file in files)
        {
            var path = SimpleTranslatorRouter.Clean(file.Path, 240);
            if (file.Covered > 0)
            {
                var status = file.Covered == file.Total ? "全部覆盖" : "部分覆盖";
                var color = file.Covered == file.Total ? Color.Green : Color.Yellow;
                LogColored(logger, $"文本汉化 | {status}：{path}（覆盖 {file.Covered}/{file.Total}，新增 {file.Added}，被同键覆盖 {file.Overridden}）", color);
            }
            else if (file.Added > 0 && file.Overridden == 0)
            {
                LogColored(logger, $"文本汉化 | 仅新增：{path}（新增 {file.Added}）", Color.Cyan);
            }
            else
            {
                LogColored(logger, $"文本汉化 | 未覆盖已有键：{path}（新增 {file.Added}，被同键覆盖 {file.Overridden}）", Color.Yellow);
            }
        }
    }

    /// <summary>
    /// 加载并应用 dialogue 本地化文本
    /// </summary>
    private void LoadDialogueLocalizations(string modPath, TranslationReport? report)
    {
        try
        {
            foreach (var locale in ServerLocales)
            {
                var dialoguePath = TranslationWorkbench.LocaleRoot(modPath, locale);
                if (!TranslationWorkbench.LocaleRoots(modPath, locale).Any(Directory.Exists)) continue;
                var dialogueFiles = TranslationWorkbench.LocaleRoots(modPath, locale).Where(Directory.Exists)
                    .SelectMany(folder => Directory.EnumerateDirectories(folder, "*", new EnumerationOptions
                        { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    .Where(path => Path.GetFileName(path).Equals("dialogue", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(TranslationFiles.EnumerateDialogueFiles).Distinct().ToArray();

                // 收集并合并所有文件的内容（后面的文件覆盖前面的），同时记录每个键的来源文件。
                // Dialogue 普通字典大小写敏感，两版均不得使用 Global 的 IgnoreCase 规则。
                var entries = new Dictionary<string, (string value, string file)>(TranslationFiles.DialogueKeyComparer);
                var localeRoot = TranslationWorkbench.LocaleRoot(modPath, locale);
                foreach (var file in dialogueFiles.Where(file =>
                             TranslationWorkbench.IsManualFileEnabled(modPath, localeRoot, file)))
                {
                    var content = Jsonc.ParseFile(file, m => logger.Warning($"[文本汉化][对话]{m}"),
                        error: m => logger.Error($"[文本汉化][对话]{m}"));
                    if (content == null || content.Count == 0)
                    {
                        continue;
                    }

                    var relativeFile = TranslationWorkbench.NormalizeRelative(dialoguePath, file);
                    foreach (var kvp in content)
                    {
                        if (entries.TryGetValue(kvp.Key, out var old) && old.file != relativeFile)
                        {
                            report?.AddConflict(kvp.Key, old.file, relativeFile);
                        }
                        entries[kvp.Key] = (kvp.Value, relativeFile);
                    }
                }

                if (entries.Count == 0)
                {
                    continue;
                }

#if SPT40
                var dialogue = databaseServer.GetTables().Templates.Dialogue;
#else
                var dialogue = templateTable.Dialogue;
#endif

                // 占位符解析用的字典引用：目标语言优先，英文兜底
                // 此时已注册本 mod 的 transformer；再次读取可获得其结果。
                // 更晚加载的 mod 仍不在当前视图内。
                IReadOnlyDictionary<string, string>? targetLocaleRef = null;
                IReadOnlyDictionary<string, string>? enLocaleRef = null;
                try
                {
                    targetLocaleRef = localeService.GetLocaleDb(locale);
                    enLocaleRef = localeService.GetLocaleDb("en");
                }
                catch
                {
                    // 字典不可用时跳过占位符解析，不影响文本写入
                }

                DialogueLocalizer.Apply(
                    dialogue,
                    locale,
                    entries,
                    targetLocaleRef,
                    enLocaleRef,
                    report,
                    m => LogColored(logger, m, Color.Green),
                    m => logger.Warning(m));
            }
        }
        catch (Exception ex)
        {
            logger.Error($"[文本汉化][对话]加载失败。{ServerLogText.Failure(ex)}");
        }
    }
}
