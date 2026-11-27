using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SPTarkov.Server.Core.Models.Utils;

namespace SimpleTranslatorCS;

public class WorkbenchAdminRequest : IRequestData
{
    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = string.Empty;
    [JsonPropertyName("stopPending")]
    public bool StopPending { get; set; }
    [JsonPropertyName("adminKey")]
    public string AdminKey { get; set; } = string.Empty;
    [JsonPropertyName("history")]
    public bool History { get; set; }
}

public sealed class WorkbenchLiveRequest : WorkbenchAdminRequest
{
    [JsonPropertyName("liveRevision")]
    public string LiveRevision { get; set; } = string.Empty;
}

public sealed class WorkbenchFileSelectionRequest : WorkbenchAdminRequest
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

public sealed class WorkbenchSaveEntry
{
    [JsonPropertyName("keepOriginal")]
    public bool KeepOriginal { get; set; }
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("translation")]
    public string Translation { get; set; } = string.Empty;
}

public sealed class WorkbenchSaveRequest : WorkbenchAdminRequest
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("revision")]
    public string Revision { get; set; } = string.Empty;

    [JsonPropertyName("profile")]
    public string Profile { get; set; } = string.Empty;

    [JsonPropertyName("combineSources")]
    public bool CombineSources { get; set; }

    [JsonPropertyName("entries")]
    public List<WorkbenchSaveEntry> Entries { get; set; } = [];

    [JsonPropertyName("submissionId")]
    public string SubmissionId { get; set; } = string.Empty;
}

public sealed class WorkbenchReviewRequest : WorkbenchAdminRequest
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = "ai";
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
    [JsonPropertyName("translation")] public string Translation { get; set; } = string.Empty;
    [JsonPropertyName("expectedTranslation")] public string? ExpectedTranslation { get; set; }
}

public sealed class WorkbenchReviewQuery : WorkbenchAdminRequest
{
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("query")] public string Query { get; set; } = string.Empty;
    [JsonPropertyName("layer")] public string Layer { get; set; } = string.Empty;
    [JsonPropertyName("sourceGroup")] public string SourceGroup { get; set; } = string.Empty;
    [JsonPropertyName("changedOnly")] public bool ChangedOnly { get; set; }
}

internal sealed class WorkbenchCatalogEntry
{
    public string Field => Id.Contains(' ') ? Id[(Id.LastIndexOf(' ') + 1)..] : "text";
    public required string Id { get; init; }
    public required string Domain { get; init; }
    public required string Origin { get; init; }
    public required string SourceGroup { get; init; }
    public required string SourceGroupName { get; init; }
    public required string English { get; init; }
    public required string Chinese { get; init; }
}

internal sealed class WorkbenchSourceGroupState
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public int EntryCount { get; init; }
}

internal sealed class WorkbenchFileState
{
    public required string Path { get; init; }
    public required string Kind { get; init; }
    public bool Enabled { get; init; }
    public int EntryCount { get; init; }
    public bool BaselineKnown { get; init; }
    public int ChangedCount { get; init; }
    public bool RequiresRestart { get; init; }
}

internal sealed class WorkbenchCatalog
{
    public required string Revision { get; init; }
    public required string GeneratedAt { get; init; }
    public required IReadOnlyList<WorkbenchCatalogEntry> Entries { get; init; }
    public required IReadOnlyList<WorkbenchSourceGroupState> SourceGroups { get; init; }
    public required IReadOnlyList<WorkbenchFileState> Files { get; init; }
    public bool RestartRequired { get; init; }
}

internal static class TranslationWorkbench
{
    private const int MaxCommitEntries = 10000;
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string? _modPath;
    private static IReadOnlyDictionary<string, string> _english = new Dictionary<string, string>();
    private static IReadOnlyDictionary<string, string> _chinese = new Dictionary<string, string>();
    private static IDictionary<string, string>? _mutableChinese;
    private static LocaleDomainIndex _domainIndex = new();
    private static WorkbenchSourceIndex _sourceIndex = WorkbenchSourceIndex.Build(string.Empty, []);
    private static DateTimeOffset _nextSourceRefreshUtc;
    private static readonly Dictionary<string, (bool Exists, string Value)> BaseChinese = new(TranslationFiles.KeyComparer);
    private static readonly HashSet<string> ManagedKeys = new(TranslationFiles.KeyComparer);
    private static bool _restartRequired;
    private static bool _dialogueRestartRequired;
    private static string _dialogueSnapshot = string.Empty;
    private static bool _fikaDetected;
    private static string _adminKey = string.Empty;
    private static int _pullCooldownSeconds = 180;
    private static readonly Dictionary<string, DateTimeOffset> NextPull = new(StringComparer.Ordinal);
    internal static Func<DateTimeOffset> UtcNow = () => DateTimeOffset.UtcNow;

    public static bool FikaDetected { get { lock (Sync) return _fikaDetected; } }
    internal static string ModPath { get { lock (Sync) { EnsureReady(); return _modPath!; } } }
    internal static WorkbenchCatalog JobCatalog()
    {
        lock (Sync) { EnsureReady(); RefreshSourceIndex(false); return BuildCatalog(); }
    }
    private static string _liveRevision = Guid.NewGuid().ToString("N");
    private static Dictionary<string, string> _publishedLocale = new(TranslationFiles.KeyComparer);
    private static HashSet<string> _publishedRemoved = new(TranslationFiles.KeyComparer);
    private static readonly Queue<LocaleChange> LocaleChanges = new();
    private sealed record LocaleChange(string Previous, Dictionary<string, string> Values, string[] Removed);
    private static void PublishLive()
    {
        var values = CurrentLiveValues();
        var removed = RemovedKeys().ToHashSet(TranslationFiles.KeyComparer);
        var changedValues = values.Where(pair => !_publishedLocale.TryGetValue(pair.Key, out var old) || old != pair.Value)
            .ToDictionary(pair => pair.Key, pair => pair.Value, TranslationFiles.KeyComparer);
        var changedRemoved = _publishedLocale.Keys.Except(values.Keys, TranslationFiles.KeyComparer)
            .Union(removed.Except(_publishedRemoved, TranslationFiles.KeyComparer), TranslationFiles.KeyComparer).ToArray();
        ServerLocaleOverlay.Publish(values, removed.ToArray());
        LocaleChanges.Enqueue(new LocaleChange(_liveRevision, changedValues, changedRemoved));
        if (LocaleChanges.Count > 16) LocaleChanges.Dequeue();
        _publishedLocale = values;
        _publishedRemoved = removed;
        _liveRevision = Guid.NewGuid().ToString("N");
        LocaleNotificationSocket.Publish(_liveRevision);
    }
    public static string LiveJson(WorkbenchLiveRequest request)
    {
        lock (Sync)
        {
            EnsureReady();
            if ((request.LiveRevision?.Length ?? 0) > 128) return Error("同步版本无效。");
            if (request.LiveRevision == _liveRevision)
                return JsonSerializer.Serialize(new { ok=true, changed=false, liveRevision=_liveRevision }, JsonOptions);
            var changes = LocaleChanges.SkipWhile(change => change.Previous != request.LiveRevision).ToArray();
            if (changes.Length > 0)
            {
                var values = new Dictionary<string, string>(TranslationFiles.KeyComparer);
                var removed = new HashSet<string>(TranslationFiles.KeyComparer);
                foreach (var change in changes)
                {
                    foreach (var key in change.Removed) { values.Remove(key); removed.Add(key); }
                    foreach (var pair in change.Values) { removed.Remove(pair.Key); values[pair.Key] = pair.Value; }
                }
                return JsonSerializer.Serialize(new { ok=true, changed=true, liveRevision=_liveRevision, locale="ch",
                    values, removedKeys=removed, restartRequired=_restartRequired }, JsonOptions);
            }
            return JsonSerializer.Serialize(new { ok=true, changed=true, liveRevision=_liveRevision, locale="ch",
                values=CurrentLiveValues(), removedKeys=RemovedKeys(), restartRequired=_restartRequired }, JsonOptions);
        }
    }
    public static bool WriteAuthRequired => true;
    public static int PullCooldownSeconds { get { lock (Sync) return _pullCooldownSeconds; } }
    internal static bool IsAuthorized(string? supplied)
    {
        lock (Sync)
            return Authorized(supplied);
    }

    public static void Initialize(
        string modPath,
        IReadOnlyDictionary<string, string> english,
        IReadOnlyDictionary<string, string> chinese,
        IEnumerable<string> itemIds,
        IEnumerable<string> questKeys,
        bool fikaDetected = false,
        string adminKey = "",
        int pullCooldownSeconds = 180,
        IEnumerable<string>? achievementKeys = null,
        IEnumerable<string>? traderIds = null,
        IEnumerable<string>? customizationIds = null,
        IEnumerable<string>? handbookKeys = null,
        bool includeOtherLocaleText = false)
    {
        lock (Sync)
        {
            if (!string.IsNullOrWhiteSpace(_modPath)
                && !Path.GetFullPath(_modPath).Equals(Path.GetFullPath(modPath), StringComparison.OrdinalIgnoreCase))
            {
                BaseChinese.Clear();
                ManagedKeys.Clear();
                LocaleChanges.Clear();
                _publishedLocale.Clear();
                _publishedRemoved.Clear();
                _liveRevision = Guid.NewGuid().ToString("N");
            }
            _modPath = modPath;
            _english = english;
            _chinese = chinese;
            _mutableChinese = chinese as IDictionary<string, string>;
            _domainIndex = new LocaleDomainIndex(itemIds, questKeys, achievementKeys, traderIds,
                customizationIds, handbookKeys, includeOtherLocaleText ? english.Keys.Where(key => key.Length <= 256) : null);
            _sourceIndex = WorkbenchSourceIndex.Build(modPath, _domainIndex.CandidateKeys);
            _nextSourceRefreshUtc = DateTimeOffset.UtcNow.AddSeconds(30);
            _fikaDetected = fikaDetected;
            _adminKey = adminKey;
            _pullCooldownSeconds = Math.Clamp(pullCooldownSeconds, 0, 86400);
            NextPull.Clear();
            _restartRequired = false;
            _dialogueRestartRequired = false;
            _dialogueSnapshot = BuildDialogueSnapshot(modPath);
            PublishLive();
        }
    }

    public static void PrepareStorage(string modPath)
    {
        lock (Sync)
        {
            TranslationStorage.Prepare(modPath);
            MigrateCache(Path.Combine(WorkbenchRoot(modPath), "records", "machine.jsonc"), CachePath(modPath, "machine"));
            MigrateCache(Path.Combine(WorkbenchRoot(modPath), "records", "ai.jsonc"), CachePath(modPath, "ai"));
        }
    }

    public static void CaptureBaseTranslations(IDictionary<string, string> target, IEnumerable<string> keys)
    {
        lock (Sync)
        {
            _mutableChinese = target;
            _chinese = target as IReadOnlyDictionary<string, string> ?? new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(target);
            foreach (var key in keys)
            {
                if (!BaseChinese.ContainsKey(key))
                    BaseChinese[key] = target.TryGetValue(key, out var value) ? (true, value) : (false, string.Empty);
                ManagedKeys.Add(key);
            }
        }
    }

    public static bool IsManualFileEnabled(string modPath, string localeRoot, string file)
    {
        var selectionRoot = LocaleRoot(modPath, Path.GetFileName(localeRoot));
        var relative = NormalizeRelative(selectionRoot, file);
        return TranslationLayout.Mode(relative) != "manual" || !LoadDisabled(modPath).Contains(relative);
    }

    internal static IReadOnlyList<string> OrderForLoading(string localeRoot, IEnumerable<string> files)
    {
        return files.Select(file => (File: file, Relative: NormalizeRelative(localeRoot, file)))
            .OrderBy(file => TranslationLayout.Rank(file.Relative))
            .ThenBy(file => TranslationLayout.Placement(file.Relative))
            .ThenBy(file => file.Relative, StringComparer.Ordinal)
            .Select(file => file.File).ToList();
    }

    public static string BuildCatalogJson()
    {
        lock (Sync)
        {
            EnsureReady();
            RefreshSourceIndex(false);
            return JsonSerializer.Serialize(BuildCatalog(), JsonOptions);
        }
    }

    public static string BuildCatalogJson(WorkbenchAdminRequest request)
    {
        lock (Sync)
            return Authorized(request.AdminKey) ? BuildCatalogJson() : Unauthorized();
    }

    public static string AccessJson(WorkbenchAdminRequest request)
    {
        lock (Sync)
        {
            EnsureReady();
            return JsonSerializer.Serialize(new { ok = true, isAdmin = Authorized(request.AdminKey),
                pullCooldownSeconds = _pullCooldownSeconds }, JsonOptions);
        }
    }

    public static string PullJson(WorkbenchAdminRequest request, string sessionId)
    {
        lock (Sync)
        {
            EnsureReady();
            var admin = Authorized(request.AdminKey);
            // The identity comes from the server request session, never from a body field.
            if (!admin && string.IsNullOrWhiteSpace(sessionId))
                return Error("拉取译文需要有效的玩家会话。");
            var now = UtcNow();
            if (!admin && NextPull.TryGetValue(sessionId, out var next) && now < next)
                return JsonSerializer.Serialize(new { ok = false, rateLimited = true,
                    retryAfterSeconds = (int)Math.Ceiling((next - now).TotalSeconds),
                    error = "拉取仍在冷却中，请稍后重试。" }, JsonOptions);
            var response = JsonSerializer.Serialize(new { ok = true, locale = "ch",
                values = CurrentLiveValues(), removedKeys = RemovedKeys(),
                restartRequired = _restartRequired, retryAfterSeconds = admin ? 0 : _pullCooldownSeconds }, JsonOptions);
            if (!admin)
            {
                foreach (var expired in NextPull.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
                    NextPull.Remove(expired);
                NextPull[sessionId] = now.AddSeconds(_pullCooldownSeconds);
            }
            return response;
        }
    }

    private static Dictionary<string, string> CurrentLiveValues() => BaseChinese.Keys.Union(ManagedKeys, TranslationFiles.KeyComparer)
        .Where(key => _chinese.ContainsKey(key)).ToDictionary(key => key, key => _chinese[key], TranslationFiles.KeyComparer);

    private static string[] RemovedKeys() => BaseChinese.Keys.Where(key => !_chinese.ContainsKey(key)).ToArray();

    public static string SetFileEnabledJson(WorkbenchFileSelectionRequest request)
    {
        lock (Sync)
        {
            EnsureReady();
            if (!Authorized(request.AdminKey))
                return Unauthorized();
            var catalog = BuildCatalog();
            var file = catalog.Files.FirstOrDefault(candidate =>
                candidate.Kind == "manual" && string.Equals(candidate.Path, request.Path, StringComparison.Ordinal));
            if (file == null)
                return Error("找不到指定的手动 JSONC 文件，已拒绝修改。");

            var disabled = LoadDisabled(_modPath!);
            var changed = request.Enabled ? disabled.Remove(file.Path) : disabled.Add(file.Path);
            if (changed)
            {
                SaveDisabled(_modPath!, disabled);
                if (file.RequiresRestart)
                    _dialogueRestartRequired = true;
                var liveApplied = ReloadFromDisk(out _);
                _restartRequired = _dialogueRestartRequired || !liveApplied;
            }
            return JsonSerializer.Serialize(new
            {
                ok = true,
                path = file.Path,
                enabled = request.Enabled,
                changed,
                restartRequired = _restartRequired
            }, JsonOptions);
        }
    }

    public static string UpdateBaselineJson(WorkbenchAdminRequest request)
    {
        lock (Sync)
        {
            EnsureReady();
            if (!Authorized(request.AdminKey))
                return Unauthorized();
            var baseline = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var file in EnumerateManualFiles(_modPath!))
            {
                var entries = ParseTranslationFile(file.FullPath, file.Dialogue);
                baseline[file.RelativePath] = entries.Keys.ToDictionary(
                    key => key,
                    key => _english.TryGetValue(key, out var source) ? source : string.Empty,
                    StringComparer.Ordinal);
            }
            AtomicWrite(BaselinePath(_modPath!), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                updatedAt = DateTimeOffset.UtcNow.ToString("O"),
                files = baseline
            }, JsonOptions));
            return JsonSerializer.Serialize(new { ok = true, files = baseline.Count }, JsonOptions);
        }
    }

    public static string SaveBatchJson(WorkbenchSaveRequest request)
    {
        lock (Sync)
        {
            EnsureReady();
            if (!Authorized(request.AdminKey))
                return Unauthorized();
            if (request.Mode is not ("ai" or "machine" or "manual"))
                return Error("保存模式必须是 ai、machine 或 manual。");
            if (request.Entries == null || request.Entries.Count == 0 || request.Entries.Count > MaxCommitEntries)
                return Error($"提交条目数量必须为 1～{MaxCommitEntries}。");

            var catalog = BuildCatalog();
            if (!string.Equals(request.Revision, catalog.Revision, StringComparison.Ordinal))
                return Error("服务端词条已刷新，本批次基于旧快照，未写入任何译文。请刷新后重试。", conflict: true);

            var eligible = catalog.Entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
            var accepted = new Dictionary<string, (string Source, string Translation, bool KeepOriginal)>(StringComparer.Ordinal);
            foreach (var entry in request.Entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Id))
                    return Error("提交中包含无效条目。");
                if (!eligible.TryGetValue(entry.Id, out var current)
                    || !string.Equals(current.English, entry.Source, StringComparison.Ordinal))
                    return Error("条目原文或缺失资格已变化，整批未保存：" + entry.Id, conflict: true);
                if (entry.Source == null || entry.Translation == null || entry.Id.Length > 256 || entry.Source.Length > 50000 || entry.Translation.Length > 50000)
                    return Error("条目过长或包含无效文本。");
                if (accepted.ContainsKey(entry.Id)) return Error("提交包含重复 ID，整批未保存。");
                if (string.IsNullOrWhiteSpace(entry.Translation))
                    return Error("译文为空，整批未保存：" + entry.Id);
                try { ServerTranslationValidation.Validate(entry.Source, entry.Translation, entry.KeepOriginal); }
                catch (InvalidOperationException ex) { return Error(ex.Message + "：" + entry.Id); }
                accepted[entry.Id] = (entry.Source, entry.Translation.Trim(), entry.KeepOriginal);
            }

            var root = LocaleRoot(_modPath!);
            var updates = new Dictionary<string, Dictionary<string, string>>(FileNameComparer);
            var cache = LoadCache(_modPath!, request.Mode, strict: true);
            var combined = request.CombineSources && request.Mode == "ai"
                ? TranslationLayout.CombinedApiFileName(accepted.Keys.Select(id => eligible[id].SourceGroup)) : null;
            foreach (var pair in accepted)
            {
                var name = combined ?? TranslationLayout.FileName(eligible[pair.Key].SourceGroup, request.Mode);
                if (!updates.TryGetValue(name, out var active))
                {
                    active = ReadDestination(root, name);
                    updates.Add(name, active);
                }
                active[pair.Key] = pair.Value.Translation;
                RecordTranslation(cache, pair.Key, name, pair.Value.Source, pair.Value.Translation, pair.Value.KeepOriginal);
            }
            var liveApplied = CommitTranslations(request.Mode, request.Profile, updates, cache);
            var activeFiles = updates.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
            return JsonSerializer.Serialize(new
            {
                ok = true,
                saved = accepted.Count,
                nextRevision = BuildCatalog().Revision,
                mode = request.Mode,
                liveApplied,
                restartRequired = _restartRequired,
                activeFile = activeFiles[0],
                activeFiles,
                cacheFile = NormalizeRelative(_modPath!, CachePath(_modPath!, request.Mode))
            }, JsonOptions);
        }
    }

    private sealed record ReviewRow(string Mode, string Id, string Domain, string SourceGroup,
        string Source, string? PreviousSource, string Translation, string SavedAt)
    {
        public bool Stale => PreviousSource != null && PreviousSource != Source;
    }

    public static string ReviewJson(WorkbenchAdminRequest request)
    {
        lock (Sync)
        {
            EnsureReady();
            if (!Authorized(request.AdminKey)) return Unauthorized();
            if (request is WorkbenchReviewQuery { Page: > 0 } query)
            {
                const int pageSize = 40;
                RefreshSourceIndex(false);
                var search = (query.Query ?? string.Empty).Trim();
                if (search.Length > 120 || (query.SourceGroup?.Length ?? 0) > 256)
                    return Error("校对筛选条件过长。");
                var rows = ReviewRows().ToArray();
                var groups = rows.Select(row => row.SourceGroup).Distinct().OrderBy(group => group, StringComparer.OrdinalIgnoreCase);
                var matches = rows.Where(row => (!query.ChangedOnly || row.Stale) &&
                    (string.IsNullOrEmpty(query.Layer) || query.Layer == row.Mode) &&
                    (string.IsNullOrEmpty(query.SourceGroup) || query.SourceGroup == row.SourceGroup) &&
                    (search.Length == 0 || new[] { row.Id, row.Source, row.Translation, row.SourceGroup }
                        .Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase))))
                    .OrderByDescending(row => row.Stale).ThenBy(row => row.Id, StringComparer.Ordinal)
                    .ThenBy(row => row.Mode, StringComparer.Ordinal).ToArray();
                var page = Math.Clamp(query.Page, 1, Math.Max(1, (matches.Length + pageSize - 1) / pageSize));
                return JsonSerializer.Serialize(new { ok = true, page, pageSize, total = matches.Length, groups,
                    entries = matches.Skip((page - 1) * pageSize).Take(pageSize) }, JsonOptions);
            }
            var entries = new List<object>();
            foreach (var mode in new[] { "ai", "machine", "manual" })
            {
                var cache = LoadCache(_modPath!, mode);
                foreach (var pair in cache.OrderByDescending(pair => pair.Value.SavedAt, StringComparer.Ordinal))
                {
                    if (!_english.TryGetValue(pair.Key, out var source)) continue;
                    var domain = _domainIndex.Classify(pair.Key);
                    if (domain == null) continue;
                    entries.Add(new { mode, id = pair.Key, domain, source,
                        translation = pair.Value.Translation,
                        stale = !string.Equals(source, pair.Value.Source, StringComparison.Ordinal),
                        savedAt = pair.Value.SavedAt });
                    if (entries.Count >= 200) break;
                }
                if (entries.Count >= 200) break;
            }
            return JsonSerializer.Serialize(new { ok = true, entries }, JsonOptions);
        }
    }

    private static IEnumerable<ReviewRow> ReviewRows()
    {
        var trackedEffective = new HashSet<string>(TranslationFiles.KeyComparer);
        foreach (var mode in new[] { "manual", "ai", "machine" })
        {
            var cache = LoadCache(_modPath!, mode);
            foreach (var (id, entry) in ManagedTranslations(_modPath!, mode, cache))
            {
                if (!_english.TryGetValue(id, out var source) || _domainIndex.Classify(id) is not { } domain) continue;
                var baseline = entry.Record;
                if ((baseline == null || baseline.Source == source) &&
                    _chinese.TryGetValue(id, out var current) && current == entry.Value)
                    trackedEffective.Add(id);
                yield return new ReviewRow(mode, id, domain, ReviewGroup(id, domain),
                    source, baseline?.Source, entry.Value, baseline?.SavedAt ?? string.Empty);
            }
        }
        var baselineFiles = LoadManualBaseline(_modPath!);
        var manualBaselines = new Dictionary<string, (string Translation, string? Source)>(TranslationFiles.KeyComparer);
        var currentRoot = LocaleRoot(_modPath!);
        var manualFiles = EnumerateManualFiles(_modPath!).Where(file => !file.Dialogue &&
            IsManualFileEnabled(_modPath!, currentRoot, file.FullPath)).ToDictionary(file => file.FullPath);
        foreach (var path in OrderForLoading(currentRoot, manualFiles.Keys))
        {
            baselineFiles.TryGetValue(manualFiles[path].RelativePath, out var sources);
            foreach (var pair in ParseTranslationFile(path, false))
                manualBaselines[pair.Key] = (pair.Value, sources?.GetValueOrDefault(pair.Key));
        }
        foreach (var pair in _chinese)
        {
            if (trackedEffective.Contains(pair.Key) || string.IsNullOrWhiteSpace(pair.Value) ||
                !_english.TryGetValue(pair.Key, out var source) || string.IsNullOrWhiteSpace(source) ||
                pair.Value.Trim().Equals(source.Trim(), StringComparison.OrdinalIgnoreCase) ||
                _domainIndex.Classify(pair.Key) is not { } domain) continue;
            var oldSource = manualBaselines.TryGetValue(pair.Key, out var baseline) && baseline.Translation == pair.Value
                ? baseline.Source : null;
            yield return new ReviewRow("published", pair.Key, domain, ReviewGroup(pair.Key, domain),
                source, oldSource, pair.Value, string.Empty);
        }
    }

    private static string ReviewGroup(string id, string domain) => domain == "locale" ? "other-locales" : _sourceIndex.GroupFor(id);

    public static string ReviewSaveJson(WorkbenchReviewRequest request)
    {
        lock (Sync)
        {
            EnsureReady();
            if (!Authorized(request.AdminKey)) return Unauthorized();
            if (request.Mode is not ("ai" or "machine" or "manual" or "published") || string.IsNullOrWhiteSpace(request.Id)
                || string.IsNullOrWhiteSpace(request.Translation)) return Error("复核参数无效。");
            if (!_english.TryGetValue(request.Id, out var source)
                || !string.Equals(source, request.Source, StringComparison.Ordinal))
                return Error("原文已变化，请刷新复核目录。", conflict: true);
            if (_domainIndex.Classify(request.Id) == null) return Error("当前文本不在可校对范围内。");
            var published = request.Mode == "published";
            var mode = published ? "manual" : request.Mode;
            var cache = LoadCache(_modPath!, mode, strict: true);
            var managed = published ? null : ManagedTranslations(_modPath!, mode, cache).GetValueOrDefault(request.Id);
            var record = managed?.Record;
            var keepOriginal = record?.KeepOriginal == true &&
                source.Trim().Equals(request.Translation.Trim(), StringComparison.OrdinalIgnoreCase);
            try { ServerTranslationValidation.Validate(source, request.Translation, keepOriginal); }
            catch (InvalidOperationException ex) { return Error(ex.Message); }
            var before = published ? _chinese.GetValueOrDefault(request.Id) : managed?.Value;
            if (before == null ||
                request.ExpectedTranslation != null && before != request.ExpectedTranslation ||
                request.ExpectedTranslation == null && (published || record == null))
                return Error("活动译文已变化，请刷新。", conflict: true);

            // Reuse an existing flat output to keep its identity; old files are left untouched.
            var name = managed != null && !managed.Path.Contains('/') ? managed.Path :
                TranslationLayout.FileName(ReviewGroup(request.Id, _domainIndex.Classify(request.Id)!), mode);
            var active = ReadDestination(LocaleRoot(_modPath!), name);
            var translation = request.Translation.Trim();
            active[request.Id] = translation;
            RecordTranslation(cache, request.Id, name, source, translation, keepOriginal);
            var updates = new Dictionary<string, Dictionary<string, string>>(FileNameComparer) { [name] = active };
            var liveApplied = CommitTranslations(mode, "manual-review", updates, cache);
            return JsonSerializer.Serialize(new { ok = true, saved = 1, liveApplied,
                restartRequired = _restartRequired }, JsonOptions);
        }
    }

    public static string ReloadJson(WorkbenchAdminRequest request)
    {
        lock (Sync)
        {
            EnsureReady();
            if (!Authorized(request.AdminKey))
                return Unauthorized();
            RefreshSourceIndex(true);
            DetectDialogueChanges();
            var liveApplied = ReloadFromDisk(out var applied);
            _restartRequired = _dialogueRestartRequired || !liveApplied;
            return JsonSerializer.Serialize(new
            {
                ok = true,
                liveApplied,
                applied,
                locale = "ch",
                values = CurrentLiveValues(),
                removedKeys = RemovedKeys(),
                restartRequired = _restartRequired,
                catalog = BuildCatalog()
            }, JsonOptions);
        }
    }

    private static WorkbenchCatalog BuildCatalog()
    {
        var tracked = new[] { "manual", "ai", "machine" }.SelectMany(mode => LoadCache(_modPath!, mode))
            .SelectMany(pair => (pair.Value.PreviousFiles?.Values.AsEnumerable() ?? []).Prepend(pair.Value)
                .Select(record => (pair.Key, Record: record)))
            .ToLookup(pair => pair.Key, pair => pair.Record, TranslationFiles.KeyComparer);
        var retained = new[] { "manual", "ai", "machine" }.SelectMany(mode =>
        {
            var cache = LoadCache(_modPath!, mode);
            if (!cache.Values.Any(record => record.KeepOriginal || record.PreviousFiles?.Values.Any(previous => previous.KeepOriginal) == true))
                return Enumerable.Empty<KeyValuePair<string, ManagedTranslation>>();
            return ManagedTranslations(_modPath!, mode, cache).Where(pair => pair.Value.Record?.KeepOriginal == true &&
                pair.Value.Record.Source.Trim().Equals(pair.Value.Value.Trim(), StringComparison.OrdinalIgnoreCase));
        }).ToLookup(pair => pair.Key, pair => pair.Value.Record!.Source, StringComparer.Ordinal);
        var entries = new List<WorkbenchCatalogEntry>();
        foreach (var pair in _english.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(pair.Value) || !ContainsLatin(pair.Value))
                continue;
            var domain = _domainIndex.Classify(pair.Key);
            if (domain == null)
                continue;
            if (retained[pair.Key].Any(source => source == pair.Value)) continue;
            var hasChineseKey = _chinese.TryGetValue(pair.Key, out var chinese);
            chinese ??= string.Empty;
            if (tracked[pair.Key].Any(record => record.Source != pair.Value && record.Translation == chinese))
                chinese = string.Empty;
            if (hasChineseKey && !string.IsNullOrWhiteSpace(chinese)
                && !string.Equals(chinese.Trim(), pair.Value.Trim(), StringComparison.Ordinal))
                continue;
            var sourceGroup = domain == "locale" ? "other-locales" : _sourceIndex.GroupFor(pair.Key);
            entries.Add(new WorkbenchCatalogEntry
            {
                Id = pair.Key,
                Domain = domain,
                Origin = sourceGroup == WorkbenchSourceIndex.BaseGroup ? "base" : "mod",
                SourceGroup = sourceGroup,
                SourceGroupName = sourceGroup == "other-locales" ? "其他语言表文本（需校对）" : sourceGroup == WorkbenchSourceIndex.BaseGroup ? "原版游戏 / 未归属" : sourceGroup,
                English = pair.Value,
                Chinese = chinese
            });
        }

        var revisionText = string.Join("\n", entries.Select(entry =>
            entry.Id + "\u001f" + entry.English + "\u001f" + entry.Chinese + "\u001f" + entry.SourceGroup));
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revisionText))).ToLowerInvariant();
        return new WorkbenchCatalog
        {
            Revision = revision,
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O"),
            Entries = entries,
            SourceGroups = entries.GroupBy(entry => entry.SourceGroup, StringComparer.OrdinalIgnoreCase)
                .Select(group => new WorkbenchSourceGroupState
                {
                    Id = group.Key,
                    Name = group.First().SourceGroupName,
                    Kind = group.Key == WorkbenchSourceIndex.BaseGroup ? "base" : "mod",
                    EntryCount = group.Count()
                })
                .OrderBy(group => group.Kind == "base" ? 0 : 1)
                .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            Files = BuildFileStates(),
            RestartRequired = _restartRequired
        };
    }

    private static IReadOnlyList<WorkbenchFileState> BuildFileStates()
    {
        var disabled = LoadDisabled(_modPath!);
        var baseline = LoadManualBaseline(_modPath!);
        var result = new List<WorkbenchFileState>();
        foreach (var file in EnumerateManualFiles(_modPath!))
        {
            var entries = ParseTranslationFile(file.FullPath, file.Dialogue);
            var known = baseline.TryGetValue(file.RelativePath, out var oldSources);
            var changed = known ? entries.Keys.Count(key =>
                !oldSources!.TryGetValue(key, out var old)
                || !string.Equals(old, _english.TryGetValue(key, out var current) ? current : string.Empty, StringComparison.Ordinal)) : 0;
            result.Add(new WorkbenchFileState
            {
                Path = file.RelativePath,
                Kind = "manual",
                Enabled = !disabled.Contains(file.RelativePath),
                EntryCount = entries.Count,
                BaselineKnown = known,
                ChangedCount = changed,
                RequiresRestart = file.Dialogue
            });
        }
        var localeRoot = LocaleRoot(_modPath!);
        var automaticFiles = EnumerateLocaleFiles(_modPath!)
            .Select(file => (FullPath: file, Path: NormalizeRelative(localeRoot, file)))
            .GroupBy(file => TranslationLayout.Mode(file.Path));
        foreach (var group in automaticFiles.Where(group => group.Key != "manual"))
        {
            var cache = LoadCache(_modPath!, group.Key);
            foreach (var file in group)
            {
                var entries = ParseTranslationFile(file.FullPath, false);
                var records = entries.Select(pair => (pair.Key, Record: FindRecord(cache, pair.Key, file.Path, pair.Value)))
                    .Where(pair => pair.Record != null).ToArray();
                result.Add(new WorkbenchFileState
                {
                    Path = file.Path,
                    Kind = group.Key,
                    Enabled = true,
                    EntryCount = entries.Count,
                    BaselineKnown = records.Length > 0,
                    ChangedCount = records.Count(pair => !_english.TryGetValue(pair.Key, out var current) || current != pair.Record!.Source)
                });
            }
        }
        return result;
    }

    private static bool ContainsLatin(string value) => value.Any(character =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');

    private static IReadOnlyList<(string FullPath, string RelativePath, bool Dialogue)> EnumerateManualFiles(string modPath)
    {
        var root = LocaleRoot(modPath);
        return LocaleRoots(modPath).Where(Directory.Exists).SelectMany(folder =>
            Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            .Where(file => Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(file).Equals(".jsonc", StringComparison.OrdinalIgnoreCase))
            .Select(file =>
            {
                var relative = NormalizeRelative(root, file);
                var dialogue = relative.Split('/').Any(part => part.Equals("dialogue", StringComparison.OrdinalIgnoreCase));
                return (file, relative, dialogue);
            })
            .Where(file => TranslationLayout.Mode(file.relative) == "manual")
            .OrderBy(file => file.relative, StringComparer.Ordinal)
            .ToList();
    }

    private static void RefreshSourceIndex(bool force)
    {
        if (!force && DateTimeOffset.UtcNow < _nextSourceRefreshUtc)
            return;
        _sourceIndex = WorkbenchSourceIndex.Build(_modPath!, _domainIndex.CandidateKeys);
        _nextSourceRefreshUtc = DateTimeOffset.UtcNow.AddSeconds(30);
    }

    private static void DetectDialogueChanges()
    {
        if (!string.Equals(_dialogueSnapshot, BuildDialogueSnapshot(_modPath!), StringComparison.Ordinal))
            _dialogueRestartRequired = true;
    }

    private static string BuildDialogueSnapshot(string modPath)
    {
        try
        {
            var disabled = LoadDisabled(modPath);
            var states = EnumerateManualFiles(modPath).Where(file => file.Dialogue).Select(file =>
            {
                var info = new FileInfo(file.FullPath);
                return file.RelativePath + "\u001f" + !disabled.Contains(file.RelativePath) + "\u001f" +
                       info.Length + "\u001f" + info.LastWriteTimeUtc.Ticks;
            });
            return string.Join("\n", states);
        }
        catch
        {
            return "<unavailable>";
        }
    }

    private static Dictionary<string, string> ParseTranslationFile(string file, bool dialogue, bool strict = false)
    {
        try
        {
            string? error = null;
            var parsed = Jsonc.ParseFile(file, null, dialogue ? TranslationFiles.DialogueKeyComparer : TranslationFiles.KeyComparer,
                message => error = message);
            if (parsed == null && strict)
                throw new InvalidDataException(error ?? "译文文件无法读取，请修复后重试：" + file);
            return parsed ?? new Dictionary<string, string>(dialogue ? TranslationFiles.DialogueKeyComparer : TranslationFiles.KeyComparer);
        }
        catch when (!strict)
        {
            return new Dictionary<string, string>(dialogue ? TranslationFiles.DialogueKeyComparer : TranslationFiles.KeyComparer);
        }
    }

    private static HashSet<string> LoadDisabled(string modPath)
    {
        var path = SelectionPath(modPath);
        if (!File.Exists(path))
            return new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(Jsonc.Normalize(File.ReadAllText(path)));
            if (!document.RootElement.TryGetProperty("disabled", out var array) || array.ValueKind != JsonValueKind.Array)
                return new HashSet<string>(StringComparer.Ordinal);
            return new HashSet<string>(array.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString()!)
                .Where(IsSafeRelativePath), StringComparer.Ordinal);
        }
        catch
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static void SaveDisabled(string modPath, HashSet<string> disabled)
    {
        AtomicWrite(SelectionPath(modPath), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            disabled = disabled.OrderBy(path => path, StringComparer.Ordinal).ToArray()
        }, JsonOptions));
    }

    private static Dictionary<string, Dictionary<string, string>> LoadManualBaseline(string modPath)
    {
        var path = BaselinePath(modPath);
        if (!File.Exists(path))
            return new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(Jsonc.Normalize(File.ReadAllText(path)));
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
                return result;
            foreach (var file in files.EnumerateObject())
            {
                if (!IsSafeRelativePath(file.Name) || file.Value.ValueKind != JsonValueKind.Object)
                    continue;
                result[file.Name] = file.Value.EnumerateObject().Where(pair => pair.Value.ValueKind == JsonValueKind.String)
                    .ToDictionary(pair => pair.Name, pair => pair.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
            }
            return result;
        }
        catch
        {
            return new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        }
    }

    private class TranslationBaseline
    {
        public bool KeepOriginal { get; set; }
        public string Source { get; set; } = string.Empty;
        public string Translation { get; set; } = string.Empty;
        public string SavedAt { get; set; } = string.Empty;
    }

    private sealed class WorkbenchCacheEntry : TranslationBaseline
    {
        public string? File { get; set; }
        public Dictionary<string, TranslationBaseline>? PreviousFiles { get; set; }
    }

    private sealed record ManagedTranslation(string Path, string Value, TranslationBaseline? Record);
    private static readonly StringComparer FileNameComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static TranslationBaseline? FindRecord(Dictionary<string, WorkbenchCacheEntry> cache, string id, string relative, string value)
    {
        if (!cache.TryGetValue(id, out var record)) return null;
        var legacy = TranslationLayout.IsLegacyOutput(relative);
        if ((record.File == null ? legacy : FileNameComparer.Equals(record.File, relative)) && record.Translation == value)
            return record;
        var previous = record.PreviousFiles?.GetValueOrDefault(relative);
        if (previous == null && legacy) previous = record.PreviousFiles?.GetValueOrDefault("");
        return previous?.Translation == value ? previous : null;
    }

    private static void RecordTranslation(Dictionary<string, WorkbenchCacheEntry> cache, string id,
        string relative, string source, string value, bool keepOriginal)
    {
        cache.TryGetValue(id, out var old);
        var previous = old?.PreviousFiles;
        if (old != null && !FileNameComparer.Equals(old.File, relative))
        {
            previous ??= new Dictionary<string, TranslationBaseline>(FileNameComparer);
            previous[old.File ?? ""] = new TranslationBaseline
            {
                Source = old.Source, Translation = old.Translation, KeepOriginal = old.KeepOriginal, SavedAt = old.SavedAt
            };
            previous.Remove(relative);
        }
        cache[id] = new WorkbenchCacheEntry
        {
            Source = source, Translation = value, KeepOriginal = keepOriginal, File = relative,
            SavedAt = DateTimeOffset.UtcNow.ToString("O"), PreviousFiles = previous
        };
    }

    private static Dictionary<string, ManagedTranslation> ManagedTranslations(string modPath, string mode,
        Dictionary<string, WorkbenchCacheEntry> cache)
    {
        var root = LocaleRoot(modPath);
        var disabled = mode == "manual" ? LoadDisabled(modPath) : new HashSet<string>();
        var trackedPaths = cache.Values.SelectMany(record =>
            (record.PreviousFiles?.Keys.AsEnumerable() ?? []).Append(record.File ?? "")).ToHashSet(FileNameComparer);
        var result = new Dictionary<string, ManagedTranslation>(TranslationFiles.KeyComparer);
        foreach (var file in EnumerateLocaleFiles(modPath))
        {
            var relative = NormalizeRelative(root, file);
            if (TranslationLayout.Mode(relative) != mode || mode == "manual" && disabled.Contains(relative)) continue;
            var legacy = TranslationLayout.IsLegacyOutput(relative);
            if (!legacy && !trackedPaths.Contains(relative)) continue;
            foreach (var pair in ParseTranslationFile(file, false))
            {
                var record = FindRecord(cache, pair.Key, relative, pair.Value);
                if (legacy || record != null)
                    result[pair.Key] = new ManagedTranslation(relative, pair.Value, record);
            }
        }
        return result;
    }

    private static Dictionary<string, string> ReadDestination(string root, string name)
    {
        var path = Path.Combine(root, name);
        if (Path.GetFileName(name) != name || Directory.Exists(root) && File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint) ||
            File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("译文保存路径必须是当前汉化目录中的普通文件。");
        return File.Exists(path) ? ParseTranslationFile(path, false, strict: true)
            : new Dictionary<string, string>(TranslationFiles.KeyComparer);
    }

    private static Dictionary<string, WorkbenchCacheEntry> LoadCache(string modPath, string mode, bool strict = false)
    {
        var path = CachePath(modPath, mode);
        if (!File.Exists(path))
            return new Dictionary<string, WorkbenchCacheEntry>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(Jsonc.Normalize(File.ReadAllText(path)));
            if (!document.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("翻译缓存缺少 entries：" + path);
            return entries.Deserialize<Dictionary<string, WorkbenchCacheEntry>>(JsonOptions)
                   ?? new Dictionary<string, WorkbenchCacheEntry>(StringComparer.Ordinal);
        }
        catch when (!strict)
        {
            return new Dictionary<string, WorkbenchCacheEntry>(StringComparer.Ordinal);
        }
    }

    private static string FormatCache(string mode, string profile, Dictionary<string, WorkbenchCacheEntry> entries) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            mode,
            profile,
            updatedAt = DateTimeOffset.UtcNow.ToString("O"),
            entries
        }, JsonOptions);

    private static bool CommitTranslations(string mode, string profile,
        Dictionary<string, Dictionary<string, string>> updates, Dictionary<string, WorkbenchCacheEntry> cache)
    {
        var root = LocaleRoot(_modPath!);
        var merged = _mutableChinese == null ? null : LoadOverlay(updates, mode, cache);
        var files = updates.ToDictionary(pair => Path.Combine(root, pair.Key),
            pair => JsonSerializer.Serialize(pair.Value, JsonOptions), FileNameComparer);
        files.Add(CachePath(_modPath!, mode), FormatCache(mode, profile, cache));
        TranslationStorage.WriteBatch(files);
        _restartRequired = _dialogueRestartRequired || merged == null;
        if (merged != null) ApplyOverlay(merged);
        else PublishLive();
        return merged != null;
    }

    private static MergedTranslations LoadOverlay(
        Dictionary<string, Dictionary<string, string>>? updates = null, string? updatedMode = null,
        Dictionary<string, WorkbenchCacheEntry>? updatedCache = null)
    {
        var modPath = _modPath!;
        var root = LocaleRoot(modPath);
        var disabled = LoadDisabled(modPath);
        var caches = new Dictionary<string, Dictionary<string, WorkbenchCacheEntry>>(StringComparer.Ordinal);
        if (updatedMode != null) caches[updatedMode] = updatedCache!;
        var files = EnumerateLocaleFiles(modPath);
        if (updates != null) files = files.Concat(updates.Keys.Select(name => Path.Combine(root, name))).Distinct(FileNameComparer).ToArray();
        var parsed = new List<(string File, IReadOnlyDictionary<string, string> Entries)>();
        foreach (var file in OrderForLoading(root, files))
        {
            var relative = NormalizeRelative(root, file);
            var mode = TranslationLayout.Mode(relative);
            if (mode == "manual" && disabled.Contains(relative)) continue;
            IReadOnlyDictionary<string, string> values = updates != null && updates.TryGetValue(relative, out var draft) ? draft : ParseTranslationFile(file, false, strict: true);
            if (values.Count == 0) continue;
            var filtered = values;
            if (TranslationLayout.MayBeTracked(relative))
            {
                if (!caches.TryGetValue(mode, out var cache)) caches[mode] = cache = LoadCache(modPath, mode, strict: true);
                filtered = FilterTrackedEntries(relative, values, _english, cache);
            }
            if (filtered.Count > 0) parsed.Add((relative, filtered));
        }
        return TranslationMerger.Merge(parsed);
    }

    private static bool ReloadFromDisk(out int applied)
    {
        applied = 0;
        if (_mutableChinese == null || string.IsNullOrWhiteSpace(_modPath)) return false;
        applied = ApplyOverlay(LoadOverlay());
        return true;
    }

    private static int ApplyOverlay(MergedTranslations merged)
    {
        var applied = 0;
        var source = new Dictionary<string, string>(_mutableChinese!, TranslationFiles.KeyComparer);
        foreach (var pair in BaseChinese)
            if (pair.Value.Exists) source[pair.Key] = pair.Value.Value;
            else source.Remove(pair.Key);
        foreach (var pair in merged.Values)
            source[pair.Key] = pair.Value;
        foreach (var key in ManagedKeys.Union(merged.Values.Keys, TranslationFiles.KeyComparer).ToArray())
        {
            if (!BaseChinese.ContainsKey(key))
                BaseChinese[key] = _mutableChinese!.TryGetValue(key, out var current) ? (true, current) : (false, string.Empty);
            if (merged.Values.TryGetValue(key, out var translated))
            {
                var value = ItemPlaceholders.Resolve(translated, source, _english);
                _mutableChinese![key] = value;
                applied++;
            }
            else if (BaseChinese[key].Exists)
            {
                _mutableChinese![key] = BaseChinese[key].Value;
            }
            else
            {
                _mutableChinese!.Remove(key);
            }
        }
        ManagedKeys.Clear();
        ManagedKeys.UnionWith(merged.Values.Keys);
        PublishLive();
        return applied;
    }

    private static bool Authorized(string? supplied) => WorkbenchAccess.FixedEquals(_adminKey, supplied);

    private static string Unauthorized() => Error(
        "此操作需要 SimpleTranslatorCS 管理员密钥，请查看服务端 workbench-admin.jsonc。普通玩家可按冷却时间手动拉取译文。",
        unauthorized: true);

    private static void MigrateCache(string legacyPath, string destinationPath)
    {
        if (!File.Exists(legacyPath))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (!File.Exists(destinationPath))
        {
            File.Move(legacyPath, destinationPath);
            return;
        }
        var merged = ReadCacheFile(legacyPath);
        foreach (var pair in ReadCacheFile(destinationPath))
            merged[pair.Key] = pair.Value;
        var mode = destinationPath.Contains(Path.DirectorySeparatorChar + "API" + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) ? "ai" : "machine";
        AtomicWrite(destinationPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            mode,
            profile = "migrated",
            updatedAt = DateTimeOffset.UtcNow.ToString("O"),
            entries = merged
        }, JsonOptions));
        File.Delete(legacyPath);
    }

    private static Dictionary<string, WorkbenchCacheEntry> ReadCacheFile(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(Jsonc.Normalize(File.ReadAllText(path)));
            if (!document.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, WorkbenchCacheEntry>(StringComparer.Ordinal);
            return entries.Deserialize<Dictionary<string, WorkbenchCacheEntry>>(JsonOptions)
                   ?? new Dictionary<string, WorkbenchCacheEntry>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, WorkbenchCacheEntry>(StringComparer.Ordinal);
        }
    }

    private static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    private static string Error(string message, bool conflict = false, bool unauthorized = false) => JsonSerializer.Serialize(new
    {
        ok = false,
        error = message,
        conflict,
        unauthorized
    }, JsonOptions);

    private static void EnsureReady()
    {
        if (string.IsNullOrWhiteSpace(_modPath))
            throw new InvalidOperationException("SimpleTranslatorCS 尚未完成词条扫描，请稍后刷新。");
    }

    private static bool IsSafeRelativePath(string path) => !string.IsNullOrWhiteSpace(path)
        && !Path.IsPathRooted(path)
        && !path.Replace('\\', '/').Split('/').Any(part => part == "..");

    internal static string NormalizeRelative(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        // Separate identities prevent an old and a current file with the same name sharing a switch.
        if (relative.StartsWith("../../db/locales/", StringComparison.Ordinal))
        {
            var parts = relative.Split('/');
            if (parts.Length >= 6) return "@legacy/" + string.Join("/", parts.Skip(5));
        }
        return relative;
    }

    internal static string LocaleRoot(string modPath, string locale = "ch") => Path.Combine(modPath, "translations", locale);
    internal static IReadOnlyList<string> LocaleRoots(string modPath, string locale = "ch") =>
        [Path.Combine(modPath, "db", "locales", locale), LocaleRoot(modPath, locale)];
    internal static IReadOnlyList<string> EnumerateLocaleFiles(string modPath, string locale = "ch") =>
        OrderForLoading(LocaleRoot(modPath, locale), LocaleRoots(modPath, locale).SelectMany(TranslationFiles.EnumerateLocaleFiles));
    private static string WorkbenchRoot(string modPath) => Path.Combine(modPath, "cache", "workbench");
    private static string SelectionPath(string modPath) => Path.Combine(WorkbenchRoot(modPath), "manual-files.jsonc");
    private static string BaselinePath(string modPath) => Path.Combine(WorkbenchRoot(modPath), "baselines", "manual.jsonc");
    private static string CachePath(string modPath, string mode) => Path.Combine(WorkbenchRoot(modPath), "records",
        mode == "ai" ? "API" : mode == "manual" ? "Manual" : "MT", "records.jsonc");
    internal static Func<string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>>
        StartupFilter(string modPath, IReadOnlyDictionary<string, string> english)
    {
        var root = LocaleRoot(modPath);
        var caches = new Dictionary<string, Dictionary<string, WorkbenchCacheEntry>>(StringComparer.Ordinal);
        return (file, entries) =>
        {
            var relative = NormalizeRelative(root, file);
            if (!TranslationLayout.MayBeTracked(relative)) return entries;
            var mode = TranslationLayout.Mode(relative);
            if (!caches.TryGetValue(mode, out var cache)) caches[mode] = cache = LoadCache(modPath, mode, strict: true);
            return FilterTrackedEntries(relative, entries, english, cache);
        };
    }

    private static IReadOnlyDictionary<string, string> FilterTrackedEntries(string relative,
        IReadOnlyDictionary<string, string> entries, IReadOnlyDictionary<string, string> english,
        Dictionary<string, WorkbenchCacheEntry> cache)
    {
        if (cache.Count == 0) return entries;
        return entries.Where(pair => FindRecord(cache, pair.Key, relative, pair.Value) is not { } saved ||
            english.TryGetValue(pair.Key, out var current) && current == saved.Source)
            .ToDictionary(pair => pair.Key, pair => pair.Value, TranslationFiles.KeyComparer);
    }
}
