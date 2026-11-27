using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleTranslatorCS;

public sealed class ContributionDecisionRequest : WorkbenchAdminRequest
{
    [JsonPropertyName("submissionId")] public string SubmissionId { get; set; } = string.Empty;
    [JsonPropertyName("approve")] public bool Approve { get; set; }
    [JsonPropertyName("translation")] public string Translation { get; set; } = string.Empty;
}

public sealed class ContributionReceiptRequest : WorkbenchAdminRequest
{
    [JsonPropertyName("submissionId")] public string SubmissionId { get; set; } = string.Empty;
}

internal sealed class ContributionReceipt
{
    public string SessionId { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public List<ContributionReceiptEntry> Results { get; set; } = [];
}

internal sealed class ContributionReceiptEntry
{
    public string Id { get; set; } = string.Empty;
    public string CandidateId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

internal sealed class PendingContribution
{
    public string SubmissionId { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Translation { get; set; } = string.Empty;
    public string Player { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string SubmittedAt { get; set; } = string.Empty;
    public bool KeepOriginal { get; set; }
    public string BatchId { get; set; } = string.Empty;
}

internal sealed class ContributionFile
{
    public int SchemaVersion { get; set; } = 1;
    public List<PendingContribution> Entries { get; set; } = [];
    public Dictionary<string, ContributionReceipt> Receipts { get; set; } = [];
}

internal static class WorkbenchContributions
{
    private const int MaxPending = 1000;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, DateTimeOffset> NextManual = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static string FilePath => Path.Combine(TranslationWorkbench.ModPath, "cache", "contributions", "pending-contributions.json");

    internal static int RemainingCapacity()
    {
        lock (Sync)
        {
            var catalog = TranslationWorkbench.JobCatalog().Entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
            return Math.Max(0, MaxPending - Load().Entries.Count(entry => catalog.TryGetValue(entry.Id, out var source) && source.English == entry.Source));
        }
    }

    internal static IReadOnlyList<WorkbenchCatalogEntry> FilterAvailable(IEnumerable<WorkbenchCatalogEntry> entries)
    {
        lock (Sync)
        {
            var pending = Load().Entries;
            return entries.Where(entry => !pending.Any(row => row.Id == entry.Id && row.Source == entry.English)).ToList();
        }
    }

    internal static string CatalogJson(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return Error("需要有效的玩家会话。");
        var catalog = TranslationWorkbench.JobCatalog();
        var available = FilterAvailable(catalog.Entries);
        var groups = catalog.SourceGroups.Select(group => new
        {
            group.Id, group.Name, group.Kind,
            EntryCount = available.Count(entry => entry.SourceGroup == group.Id)
        }).Where(group => group.EntryCount > 0).ToArray();
        return JsonSerializer.Serialize(new { ok = true, catalog.Revision, catalog.GeneratedAt,
            entries = available, sourceGroups = groups, files = Array.Empty<object>(),
            catalog.RestartRequired }, Options);
    }

    internal static string QueueBatch(string mode, IReadOnlyList<WorkbenchSaveEntry> entries,
        string sessionId, string playerName)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return Error("需要有效的玩家会话。");
        lock (Sync)
        {
            var file = Load();
            var results = QueueEntries(file, mode, entries, sessionId, playerName);
            Save(file);
            return ReceiptResponse(results);
        }
    }

    private static List<ContributionReceiptEntry> QueueEntries(ContributionFile file, string mode,
        IReadOnlyList<WorkbenchSaveEntry> entries, string sessionId, string playerName, string batchId = "")
    {
        var eligible = TranslationWorkbench.JobCatalog().Entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        foreach (var stale in file.Entries.Where(row => !eligible.TryGetValue(row.Id, out var source) || source.English != row.Source).ToArray())
        {
            SetOutcome(file, stale, "stale", "原文变化或已有正式译文");
            file.Entries.Remove(stale);
        }
        var results = new List<ContributionReceiptEntry>();
        foreach (var entry in entries)
        {
            var reason = file.Entries.Count >= MaxPending ? "审核队列已满" :
                file.Entries.Any(row => row.Id == entry.Id && row.Source == entry.Source) ? "已有待审核投稿" :
                !eligible.TryGetValue(entry.Id, out var source) || source.English != entry.Source || entry.Translation.Length > 50000
                    ? "原文或缺译状态已变化" : string.Empty;
            if (reason.Length > 0)
            {
                results.Add(new() { Id = entry.Id, State = "skipped", Reason = reason });
                continue;
            }
            var candidateId = Guid.NewGuid().ToString("N");
            file.Entries.Add(new PendingContribution
            {
                SubmissionId = candidateId, Mode = mode, Id = entry.Id, BatchId = batchId,
                Source = entry.Source, Translation = entry.Translation, KeepOriginal = entry.KeepOriginal,
                SessionId = sessionId, Player = SimpleTranslatorRouter.Clean(playerName, 80),
                SubmittedAt = TranslationWorkbench.UtcNow().ToString("O")
            });
            results.Add(new() { Id = entry.Id, CandidateId = candidateId, State = "pending" });
        }
        return results;
    }

    private static void SetOutcome(ContributionFile file, PendingContribution entry, string state, string reason = "")
    {
        if (string.IsNullOrEmpty(entry.BatchId) || !file.Receipts.TryGetValue(entry.BatchId, out var receipt)) return;
        var result = receipt.Results.Find(row => row.CandidateId == entry.SubmissionId);
        if (result == null) return;
        result.State = state;
        result.Reason = reason;
    }

    private static string ReceiptResponse(List<ContributionReceiptEntry> results)
    {
        var savedIds = results.Where(row => row.CandidateId.Length > 0).Select(row => row.Id).ToArray();
        return JsonSerializer.Serialize(new { ok = true, saved = savedIds.Length, savedIds, results }, Options);
    }

    internal static string ReceiptJson(ContributionReceiptRequest request, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return Unauthorized();
        lock (Sync)
        {
            var receipts = Load().Receipts;
            if (string.IsNullOrEmpty(request.SubmissionId))
                return JsonSerializer.Serialize(new { ok = true, entries = receipts.Reverse()
                    .Where(row => row.Value.SessionId == sessionId).Take(5)
                    .Select(row => new { submissionId = row.Key, results = row.Value.Results }) }, Options);
            if (!Guid.TryParseExact(request.SubmissionId, "N", out _)) return Unauthorized();
            return receipts.TryGetValue(request.SubmissionId, out var receipt) && receipt.SessionId == sessionId
                ? ReceiptResponse(receipt.Results) : JsonSerializer.Serialize(new { ok = true, found = false });
        }
    }

    internal static string PendingJson(WorkbenchAdminRequest request)
    {
        if (!TranslationWorkbench.IsAuthorized(request.AdminKey)) return Unauthorized();
        lock (Sync)
        {
            var current = TranslationWorkbench.JobCatalog().Entries
                .ToDictionary(entry => entry.Id, StringComparer.Ordinal);
            var entries = Load().Entries.OrderByDescending(entry => entry.SubmittedAt, StringComparer.Ordinal)
                .Take(200).Select(entry => new
                {
                    entry.SubmissionId, entry.Mode, entry.Id, entry.Source,
                    entry.Translation, entry.Player, entry.SubmittedAt, entry.KeepOriginal,
                    stale = !current.TryGetValue(entry.Id, out var source) || source.English != entry.Source
                }).ToArray();
            return JsonSerializer.Serialize(new { ok = true, entries }, Options);
        }
    }

    internal static string DecideJson(ContributionDecisionRequest request)
    {
        if (!TranslationWorkbench.IsAuthorized(request.AdminKey)) return Unauthorized();
        lock (Sync)
        {
            var file = Load();
            var entry = file.Entries.FirstOrDefault(row => row.SubmissionId == request.SubmissionId);
            if (entry == null) return Error("候选译文已处理，请刷新列表。");
            if (request.Approve)
            {
                if (string.IsNullOrWhiteSpace(request.Translation) || request.Translation.Length > 50000)
                    return Error("候选译文为空或过长。");
                var translation = request.Translation.Trim();
                try { ServerTranslationValidation.Validate(entry.Source, translation, entry.KeepOriginal); }
                catch (InvalidOperationException ex) { return Error(ex.Message); }
                var catalog = TranslationWorkbench.JobCatalog();
                var saved = TranslationWorkbench.SaveBatchJson(new WorkbenchSaveRequest
                {
                    AdminKey = request.AdminKey, Mode = entry.Mode, Revision = catalog.Revision,
                    Profile = "contribution-review", Entries = [new WorkbenchSaveEntry
                    { Id = entry.Id, Source = entry.Source, Translation = translation, KeepOriginal = entry.KeepOriginal }]
                });
                using var result = JsonDocument.Parse(saved);
                if (!result.RootElement.GetProperty("ok").GetBoolean()) return saved;
            }
            SetOutcome(file, entry, request.Approve ? "approved" : "rejected");
            file.Entries.Remove(entry);
            Save(file);
            return JsonSerializer.Serialize(new { ok = true, approved = request.Approve, saved = request.Approve ? 1 : 0 });
        }
    }

    internal static string SubmitManualJson(WorkbenchSaveRequest request, string sessionId, string player, bool queueOnly = false)
    {
        request.Mode = "manual";
        request.Profile = "web-manual-v1";
        var admin = TranslationWorkbench.IsAuthorized(request.AdminKey);
        if (admin && !queueOnly) return TranslationWorkbench.SaveBatchJson(request);
        if (string.IsNullOrWhiteSpace(sessionId)) return Unauthorized();
        if (!admin && !ServerTranslationProviders.Load().Config.AllowPlayerContributions) return Error("管理员已关闭玩家贡献。");
        if (request.Entries == null || request.Entries.Count is < 1 or > 30) return Error("玩家每次可回填 1～30 条。");
        var batchId = request.SubmissionId ?? string.Empty;
        if (batchId.Length > 0 && !Guid.TryParseExact(batchId, "N", out _)) return Error("投稿批次编号无效。");
        lock (Sync)
        {
            var file = Load();
            var fingerprint = batchId.Length == 0 ? string.Empty : Convert.ToHexString(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(new { request.Revision, request.Entries }, Options)));
            if (batchId.Length > 0 && file.Receipts.TryGetValue(batchId, out var previous))
                return previous.SessionId == sessionId && previous.Fingerprint == fingerprint
                    ? ReceiptResponse(previous.Results) : Error("投稿批次编号已被使用。");
            var now = TranslationWorkbench.UtcNow();
            foreach (var key in NextManual.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray()) NextManual.Remove(key);
            if (NextManual.TryGetValue(sessionId, out var until) && until > now) return Error("人工提交间隔为 60 秒。");
            var catalog = TranslationWorkbench.JobCatalog();
            if (request.Revision != catalog.Revision) return Error("原文目录已变化，请重新导出。");
            var current = catalog.Entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in request.Entries)
            {
                if (entry == null || entry.Id == null || !ids.Add(entry.Id) || !current.TryGetValue(entry.Id, out var source) || source.English != entry.Source ||
                    entry.Translation == null || entry.Translation.Length > 50000) return Error("回填含未知、重复或已变化的条目，整批未提交。");
                try { ServerTranslationValidation.Validate(entry.Source, entry.Translation, entry.KeepOriginal); }
                catch (InvalidOperationException ex) { return Error(ex.Message); }
            }
            var results = QueueEntries(file, "manual", request.Entries, sessionId, player, batchId);
            if (batchId.Length > 0)
            {
                foreach (var expired in file.Receipts.Where(row => row.Value.Results.All(result => result.State != "pending"))
                             .Take(Math.Max(0, file.Receipts.Count - 499)).Select(row => row.Key).ToArray())
                    file.Receipts.Remove(expired);
                file.Receipts.Add(batchId, new() { SessionId = sessionId, Fingerprint = fingerprint, Results = results });
            }
            Save(file);
            NextManual[sessionId] = now.AddSeconds(60);
            return ReceiptResponse(results);
        }
    }

    private static ContributionFile Load()
    {
        if (!File.Exists(FilePath)) return new ContributionFile();
        return JsonSerializer.Deserialize<ContributionFile>(File.ReadAllText(FilePath), Options)
            ?? throw new InvalidDataException("候选译文队列文件为空。");
    }

    private static void Save(ContributionFile file)
    {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(file, Options), new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { ok = false, error = message });
    private static string Unauthorized() => JsonSerializer.Serialize(new
    { ok = false, unauthorized = true, error = "管理员密钥无效。" });
}
