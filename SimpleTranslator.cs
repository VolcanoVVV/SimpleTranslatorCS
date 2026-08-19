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

        var partiallyCoveredFiles = new List<(string fileName, int covered, int total)>();
        var nonCoveredFiles = new List<string>();
        var fullyCoveredFiles = new List<string>();

        var addedLocalesCountByLang = new Dictionary<string, int>();

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

            // 跨文件键冲突追踪：键 -> 首次出现的文件（后者覆盖前者）
            var keyFirstSeen = new Dictionary<string, string>();

            foreach (var file in Directory.EnumerateFiles(localePath, "*.*", SearchOption.AllDirectories)
                         .Where(f => new[] { ".json", ".jsonc" }.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
                         .Where(f => !f.Contains("\\dialogue\\") && !f.Contains("/dialogue/")))  // 排除 dialogue 目录
            {
                loadedFileCount++;

                var content = Jsonc.ParseFile(file, m => logger.Error($"[文本汉化]{m}"));
                if (content == null || content.Count == 0)
                {
                    continue;
                }

                var totalFields = content.Count;
                var coveredFields = 0;
                var relativeFile = System.IO.Path.GetRelativePath(localePath, file);
                foreach (var kvp in content)
                {
                    // 键存在于英文或目标语言现有字典 = 覆盖了一条游戏原文 = 生效
                    var covered = referenceLocales.ContainsKey(kvp.Key)
                                  || (targetLocaleRef?.ContainsKey(kvp.Key) ?? false);
                    if (covered)
                    {
                        coveredFields++;
                    }
                    report?.AddLocale(relativeFile, kvp.Key, kvp.Value, covered);

                    if (keyFirstSeen.TryGetValue(kvp.Key, out var firstFile))
                    {
                        if (firstFile != relativeFile)
                        {
                            report?.AddConflict(kvp.Key, firstFile, relativeFile);
                        }
                    }
                    else
                    {
                        keyFirstSeen[kvp.Key] = relativeFile;
                    }
                }

                // 目标语言容器必须存在
#if SPT40
                var localeGlobal = databaseServer.GetTables().Locales.Global;
#else
                var localeGlobal = localeTable.Global;
#endif
                if (!localeGlobal.TryGetValue(locale, out var lazyTarget))
                {
                    logger.Warning($"目标语言 {locale} 在数据库中不存在，跳过该语言覆盖");
                    continue;
                }

                // 开发模式：用当前可见的字典预览占位符解析结果，失败项记入报告
                // （最终以运行时 transformer 的警告日志为准，mod 物品的预览可能误报）
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
                    foreach (var p in content)
                    {
                        preview[p.Key] = p.Value;
                    }
                    foreach (var kvp in content)
                    {
                        var unresolved = new List<string>();
                        ItemPlaceholders.Resolve(kvp.Value, preview, referenceLocales, unresolved);
                        foreach (var id in unresolved)
                        {
                            report.AddUnresolvedPlaceholder(relativeFile, kvp.Key, id);
                        }
                    }
                }

                var fileName = System.IO.Path.GetFileName(file);

                // 在目标语言上追加 transformer 以合并我们的更改（加入空字典保护）
                lazyTarget.AddTransformer(targetDict =>
                {
#if SPT40
                    // 4.0 的 Global 字典就是普通的 Dictionary<string, string>
                    targetDict ??= new Dictionary<string, string>();
#else
                    targetDict ??= new GlobalLocaleDictionary();
#endif

                    // 第一遍：先原样写入全部条目，保证第二遍解析占位符时
                    // 能看到本文件自带的 "{id} Name" 等翻译条目
                    foreach (var kvp in content)
                    {
                        targetDict[kvp.Key] = kvp.Value;
                    }

                    // 第二遍：把 ${mongoId} 占位符替换为物品名称
                    // transformer 在 locale 字典真正构建时才执行，此时其他 mod
                    // 添加的物品名已进入字典，mod 物品也能正确解析
                    foreach (var kvp in content)
                    {
                        var unresolved = kvp.Value.Contains("${") ? new List<string>() : null;
                        targetDict[kvp.Key] = ItemPlaceholders.Resolve(kvp.Value, targetDict, referenceLocales, unresolved);
                        if (unresolved != null)
                        {
                            foreach (var id in unresolved)
                            {
                                logger.Warning($"[文本汉化]占位符找不到物品名: {id}（{fileName} 的键 \"{kvp.Key}\"），游戏内将显示为裸 id");
                            }
                        }
                    }
                    return targetDict;
                });

                if (!addedLocalesCountByLang.ContainsKey(locale))
                {
                    addedLocalesCountByLang[locale] = 0;
                }
                addedLocalesCountByLang[locale] += coveredFields;

                if (coveredFields == 0)
                {
                    nonCoveredFiles.Add(fileName);
                }
                else if (coveredFields < totalFields)
                {
                    partiallyCoveredFiles.Add((fileName, coveredFields, totalFields));
                }
                else
                {
                    fullyCoveredFiles.Add(fileName);
                }
            }
        }

        LogStatistics(loadedFileCount, addedLocalesCountByLang, fullyCoveredFiles, partiallyCoveredFiles, nonCoveredFiles, logger);

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
        Dictionary<string, int> addedLocalesCountByLang,
        List<string> fullyCoveredFiles,
        List<(string fileName, int covered, int total)> partiallyCoveredFiles,
        List<string> nonCoveredFiles,
        ISptLogger<SimpleTranslator> logger)
    {
        logger.Info($"[文本汉化]总计 [{loadedFileCount}] 个汉化文本文件加载");

        foreach (var locale in ServerLocales)
        {
            var count = addedLocalesCountByLang.TryGetValue(locale, out var c) ? c : 0;
            logger.Info($"[文本汉化]  - 总计: {count} 字段");
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
                logger.Info($"  - {f.fileName} ({f.covered}/{f.total} 字段)");
            }
            logger.Info(string.Empty);
        }

        if (nonCoveredFiles.Count > 0)
        {
            LogColored(logger, $"[文本汉化]未覆盖 文本列表:", Color.Red);
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

                var dialogueFiles = Directory.EnumerateFiles(dialoguePath, "*.json*", SearchOption.AllDirectories)
                    .Where(f => new[] { ".json", ".jsonc" }.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
                    .ToList();

                // 收集并合并所有文件的内容（后面的文件覆盖前面的），同时记录每个键的来源文件
                var entries = new Dictionary<string, (string value, string file)>();
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
