using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
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
        ?? "1.4.0";
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
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
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
    TemplateTable templateTable) : IOnLoad
#endif
{
    // 目标语言（本 mod 只服务中文用户）
    private static readonly string[] ServerLocales = { "ch" };

    // devMode 文件监听器需要一直存活，用静态字段持有防止被 GC
    private static FileSystemWatcher? _fileWatcher;

#if SPT40
    // 4.0 的 IOnLoad 是 OnLoad()，4.1 是 OnLoadAsync(CancellationToken)
    public Task OnLoad()
#else
    public Task OnLoadAsync(CancellationToken cancellationToken)
#endif
    {
        var loadedFileCount = 0;

        var partiallyCoveredFiles = new List<(string fileName, int covered, int added, int total)>();
        var nonCoveredFiles = new List<string>();
        var fullyCoveredFiles = new List<string>();

        // 语言级写入统计：(写入键数, 覆盖基底, 新增键)
        var writtenLocalesByLang = new Dictionary<string, (int written, int baseCovered, int added)>();

        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());

        // 开发/日志模式：mod 目录下 config.jsonc 中 devMode=true 时，
        // 加载完成后导出词条生效报告，并监听翻译文件变更给出重启提示
        var config = ModConfig.Load(modPath, m => logger.Warning($"[文本汉化]{m}"));
        var report = config.DevMode ? new TranslationReport() : null;
        if (config.DevMode)
        {
            logger.Info("[文本汉化]开发模式已启用，加载完成后将导出词条生效报告");
        }

        foreach (var locale in ServerLocales)
        {
            var localePath = System.IO.Path.Combine(modPath, "db", "locales", locale);
            if (!Directory.Exists(localePath))
            {
                logger.Info($"[文本汉化]未找到汉化文件夹目录: {locale}, 请检查是否在 SPT/user/mods/SimpleTranslatorCS/db/locales 中已经创建 ch 文件夹");
                continue;
            }

            if (config.DevMode)
            {
                StartFileWatcher(localePath, logger);
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
            foreach (var file in TranslationFiles.EnumerateLocaleFiles(localePath))
            {
                loadedFileCount++;

                var content = Jsonc.ParseFile(file, m => logger.Error($"[文本汉化]{m}"));
                if (content == null || content.Count == 0)
                {
                    continue;
                }
                parsedFiles.Add((System.IO.Path.GetRelativePath(localePath, file), content));
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
                    report?.AddLocale(relativeFile, kvp.Key, kvp.Value, baseExists, isWinner, winnerFile);
                }

                if (!targetAvailable)
                {
                    continue;
                }

                var fileName = System.IO.Path.GetFileName(relativeFile);
                if (coveredFields == 0)
                {
                    nonCoveredFiles.Add(fileName);
                }
                else if (coveredFields < totalFields)
                {
                    partiallyCoveredFiles.Add((fileName, coveredFields, newKeyFields, totalFields));
                }
                else
                {
                    fullyCoveredFiles.Add(fileName);
                }
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

            if (merged.Values.Count == 0)
            {
                continue;
            }

            if (!targetAvailable)
            {
                logger.Warning($"目标语言 {locale} 在数据库中不存在，跳过该语言覆盖");
                continue;
            }
            var lazyTarget = localeGlobal[locale];

            // 开发模式：基于合并后的完整字典预览占位符解析结果（跨文件引用可见，比逐文件预览更准），
            // 缺失归属到该键的胜出文件；最终以运行时 transformer 的警告日志为准，mod 物品的预览可能误报
            if (report != null)
            {
                var preview = new Dictionary<string, string>(referenceLocales);
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
            lazyTarget.AddTransformer(targetDict =>
            {
#if SPT40
                // 4.0 的 Global 字典就是普通的 Dictionary<string, string>
                targetDict ??= new Dictionary<string, string>();
#else
                targetDict ??= new GlobalLocaleDictionary();
#endif

                // 先原样写入全部合并值：随后解析占位符时目标字典已含全部合并原始值
                // 与游戏/其他 mod 条目，跨文件引用因此可见
                foreach (var kvp in merged.Values)
                {
                    targetDict[kvp.Key] = kvp.Value;
                }

                // 再统一对合并后的每个键做深度占位符解析
                // （同一键的同一缺失 id 只警告一次，归属到胜出文件）
                foreach (var kvp in merged.Values)
                {
                    var unresolved = kvp.Value.Contains("${") ? new List<string>() : null;
                    targetDict[kvp.Key] = ItemPlaceholders.Resolve(kvp.Value, targetDict, referenceLocales, unresolved);
                    if (unresolved != null)
                    {
                        foreach (var id in unresolved)
                        {
                            logger.Warning($"[文本汉化]占位符找不到物品名: {id}（{merged.WinningFile(kvp.Key)} 的键 \"{kvp.Key}\"），游戏内将显示为裸 id");
                        }
                    }
                }
                return targetDict;
            });
        }

        LogStatistics(loadedFileCount, writtenLocalesByLang, fullyCoveredFiles, partiallyCoveredFiles, nonCoveredFiles, logger);

        // 处理 dialogue 本地化
        LoadDialogueLocalizations(modPath, report);

        // 开发模式：导出词条生效报告
        report?.WriteTo(System.IO.Path.Combine(modPath, config.ReportFile), m => logger.Info(m), m => logger.Error(m));

        return Task.CompletedTask;
    }

    /// <summary>
    /// 开发模式：监听翻译目录的文件变更，提示译者修改需重启服务器生效。
    /// （locale 字典构建后即固化，真正的热重载不可靠，故只做提示）
    /// </summary>
    private static void StartFileWatcher(string localePath, ISptLogger<SimpleTranslator> logger)
    {
        if (_fileWatcher != null)
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
                logger.Info($"[文本汉化]检测到翻译文件变更: {e.FullPath} —— 重启服务器后生效");
            }

            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnChanged;

            _fileWatcher = watcher;
        }
        catch (Exception ex)
        {
            logger.Warning($"[文本汉化]翻译文件监听启动失败: {ex.Message}");
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

    /// <summary>
    /// 记录和显示统计信息
    /// </summary>
    private static void LogStatistics(
        int loadedFileCount,
        Dictionary<string, (int written, int baseCovered, int added)> writtenLocalesByLang,
        List<string> fullyCoveredFiles,
        List<(string fileName, int covered, int added, int total)> partiallyCoveredFiles,
        List<string> nonCoveredFiles,
        ISptLogger<SimpleTranslator> logger)
    {
        logger.Info($"[文本汉化]总计 [{loadedFileCount}] 个汉化文本文件加载");

        foreach (var locale in ServerLocales)
        {
            var (written, baseCovered, added) = writtenLocalesByLang.TryGetValue(locale, out var s) ? s : (0, 0, 0);
            logger.Info($"[文本汉化]  - {locale}: 写入 {written} 个键（覆盖基底 {baseCovered} / 新增键 {added}）");
            logger.Info(string.Empty);
        }

        if (fullyCoveredFiles.Count > 0)
        {
            LogColored(logger, $"[文本汉化]完全覆盖 文本列表:", Color.Green);
            foreach (var f in fullyCoveredFiles)
            {
                logger.Info($"  - {f} ");
            }
            logger.Info(string.Empty);
        }

        if (partiallyCoveredFiles.Count > 0)
        {
            LogColored(logger, $"[文本汉化]部分覆盖 文本列表:", Color.Yellow);
            foreach (var f in partiallyCoveredFiles)
            {
                var overridden = f.total - f.covered - f.added;
                logger.Info($"  - {f.fileName}（覆盖基底 {f.covered}/{f.total}；新增键 {f.added}；被同键覆盖 {overridden}）");
            }
            logger.Info(string.Empty);
        }

        if (nonCoveredFiles.Count > 0)
        {
            LogColored(logger, $"[文本汉化]未覆盖基底 文本列表（词条可能为新增键或被其他文件同键覆盖，详见报告）:", Color.Red);
            foreach (var f in nonCoveredFiles)
            {
                logger.Info($"  - {f}");
            }
            logger.Info(string.Empty);
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
                var dialoguePath = System.IO.Path.Combine(modPath, "db", "locales", locale, "dialogue");
                if (!Directory.Exists(dialoguePath))
                {
                    continue;
                }

                var dialogueFiles = TranslationFiles.EnumerateDialogueFiles(dialoguePath);

                // 收集并合并所有文件的内容（后面的文件覆盖前面的），同时记录每个键的来源文件。
                // 比较器与目标容器一致，仅大小写不同的键也会按同一键合并并报冲突
                var entries = new Dictionary<string, (string value, string file)>(TranslationFiles.KeyComparer);
                foreach (var file in dialogueFiles)
                {
                    var content = Jsonc.ParseFile(file, m => logger.Error($"[文本汉化]{m}"));
                    if (content == null || content.Count == 0)
                    {
                        continue;
                    }

                    var relativeFile = System.IO.Path.GetRelativePath(dialoguePath, file);
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
                // （dialogue 在 OnLoad 阶段直接写库，拿不到 transformer 合并后的最终字典，
                //   个别 mod 物品名缺失时会自动回退英文或裸 id）
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
                    m => logger.Info(m),
                    m => logger.Warning(m));
            }
        }
        catch (Exception ex)
        {
            logger.Error($"[Dialogue本地化]处理失败: {ex.Message}");
        }
    }
}
