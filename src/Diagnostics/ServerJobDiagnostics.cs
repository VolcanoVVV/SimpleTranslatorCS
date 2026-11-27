using System.Text;
using System.Text.Json;

namespace SimpleTranslatorCS;

internal sealed class ServerJobDiagnostics
{
    private sealed class Row
    {
        public string Id { get; init; } = "";
        public string Plugin { get; init; } = "";
        public string Field { get; init; } = "";
        public string State { get; set; } = "not_started";
        public string Reason { get; set; } = "";
    }
    private readonly object _sync = new();
    private readonly Dictionary<string, Row> _rows;
    private readonly string _path, _jobId;
    private string[] _secrets;
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private readonly List<object> _requests = [];
    private string _state = "running", _detail = "准备任务", _writeError = "";
    private string[] _currentItems = [];
    private DateTimeOffset? _finished;
    private int _requestCount, _usageResponses;
    internal readonly TranslationFoundation.PaidApiBudget Budget = new();
    private long _inputTokens, _outputTokens;
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal ServerJobDiagnostics(string jobId, IEnumerable<WorkbenchCatalogEntry> entries, ServerProviderSecrets secrets, string administratorKey = "")
    {
        _jobId = jobId;
        _path = Path.Combine(ServerTranslationLog.Root, jobId + ".json");
        _secrets = new[] { secrets.AiApiKey, secrets.MachineApiKey, secrets.MachineAppId, administratorKey }
            .Where(value => !string.IsNullOrWhiteSpace(value)).SelectMany(value => new[] { value, value.Trim() }).OrderByDescending(value => value.Length).Distinct().ToArray();
        _rows = entries.ToDictionary(entry => entry.Id, entry => new Row
            { Id = Clean(entry.Id), Plugin = Clean(entry.SourceGroupName), Field = Clean(entry.Domain) });
        Persist();
        Input("", entries.ToArray());
    }

    internal void Input(string prompt, IReadOnlyList<WorkbenchCatalogEntry> entries)
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var row = new { timeUtc = DateTimeOffset.UtcNow, prompt = Clean(prompt), items = entries.Select(entry =>
                    new { id = Clean(entry.Id), group = Clean(entry.SourceGroupName), source = Clean(entry.English) }) };
                File.AppendAllText(Path.ChangeExtension(_path, ".inputs.jsonl"), JsonSerializer.Serialize(row, new JsonSerializerOptions(Options) { WriteIndented = false }) + "\n", new UTF8Encoding(false));
            }
            catch (Exception ex) { _writeError = "输入日志写入失败：" + ServerLogText.Failure(ex); }
        }
    }

    private string Clean(string value)
    {
        foreach (var secret in _secrets) value = value.Replace(secret, "[已隐藏]", StringComparison.Ordinal);
        return value;
    }

    private string Describe(WorkbenchCatalogEntry entry) =>
        Clean(entry.SourceGroupName + " · " + ServerLogText.Domain(entry.Domain) + " · " + entry.Id);

    internal void Request(IReadOnlyList<WorkbenchCatalogEntry> entries, int characters = 0)
    {
        lock (_sync)
        {
            _requestCount++;
            _currentItems = entries.Select(Describe).ToArray();
            _detail = "等待 API 返回（本批 " + entries.Count + " 条）\n" +
                string.Join("\n", _currentItems.Take(3));
            foreach (var entry in entries) if (_rows.TryGetValue(entry.Id, out var row)) row.State = "in_progress";
            _requests.Add(new { attempt = _requestCount, at = DateTimeOffset.UtcNow, inputCharacters = characters,
                ids = entries.Select(entry => Clean(entry.Id)).ToArray() });
            Persist();
        }
    }

    internal void BeginBatch(IReadOnlyList<WorkbenchCatalogEntry> entries)
    {
        lock (_sync)
        {
            _currentItems = entries.Select(Describe).ToArray();
            _detail = "处理本批 " + entries.Count + " 条\n" + string.Join("\n", _currentItems.Take(3));
            foreach (var entry in entries) if (_rows.TryGetValue(entry.Id, out var row)) row.State = "in_progress";
            Persist();
        }
    }

    internal (long? Input, long? Output) Usage(JsonElement response)
    {
        lock (_sync)
        {
            if (!response.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return (null, null);
            long? input = usage.TryGetProperty("prompt_tokens", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var pi) ? pi : null;
            long? output = usage.TryGetProperty("completion_tokens", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out var co) ? co : null;
            if (input.HasValue || output.HasValue) _usageResponses++;
            _inputTokens += Math.Max(0, Math.Min(int.MaxValue, input ?? 0));
            _outputTokens += Math.Max(0, Math.Min(int.MaxValue, output ?? 0));
            return (input, output);
        }
    }

    internal void Phase(string phase) { lock (_sync) _detail = Clean(phase); }

    internal void Result(string id, string state, string reason = "")
    {
        lock (_sync) if (_rows.TryGetValue(id, out var row)) { row.State = state; row.Reason = Clean(reason); }
    }

    internal object Snapshot(bool full)
    {
        lock (_sync) return new { jobId = _jobId, state = _state, detail = _detail, requests = _requestCount,
            inputTokensReported = _inputTokens, outputTokensReported = _outputTokens, responsesWithUsage = _usageResponses,
            startedAt = _started, elapsedSeconds = ((_finished ?? DateTimeOffset.UtcNow) - _started).TotalSeconds, writeError = _writeError,
            currentItems = _currentItems,
            entries = full ? _rows.Values.Select(row => new { id = Clean(row.Id), plugin = Clean(row.Plugin),
                field = row.Field, state = row.State, reason = row.Reason }).ToArray() : null,
            attempts = full ? _requests.ToArray() : null };
    }

    private string LocalReport()
    {
        var report = JsonSerializer.SerializeToNode(Snapshot(true), Options)!.AsObject();
        report["stateLabel"] = ServerLogText.JobState(_state);
        foreach (var item in report["entries"]!.AsArray())
        {
            item!["stateLabel"] = ServerLogText.JobState(item["state"]!.GetValue<string>());
            item["fieldLabel"] = ServerLogText.Domain(item["field"]!.GetValue<string>());
        }
        return report.ToJsonString(Options);
    }

    internal void Finish(string state, string error = "")
    {
        lock (_sync)
        {
            _state = state;
            _finished = DateTimeOffset.UtcNow;
            _detail = error.Length > 0 ? Clean(error) : ServerLogText.JobState(state);
            _currentItems = [];
            foreach (var row in _rows.Values.Where(row => row.State == "in_progress"))
            {
                row.State = state == "cancelled" ? "interrupted" : "failed";
                row.Reason = error.Length > 0 ? Clean(error) : "任务结束前未保存本条译文";
            }
            Persist();
            var label = ServerLogText.JobState(state);
            ServerTranslationLog.Write("翻译任务 " + _jobId, "", label,
                string.IsNullOrEmpty(error) ? label + "，详情见同编号的任务报告。" : Clean(error));
            _secrets = [];
        }
    }

    internal void Persist()
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path + ".tmp", LocalReport(), new UTF8Encoding(false));
                File.Move(_path + ".tmp", _path, true);
            }
            catch (Exception ex) { _writeError = "服务端诊断写入失败：" + ServerLogText.Failure(ex); }
        }
    }
}
