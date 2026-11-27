using System.Text.Json.Serialization;

namespace SimpleTranslatorCS;

// No endpoint, provider adapter or target locale can be supplied by a player task.
public sealed class AiTaskOptions
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("endpoint")] public string? Endpoint { get; set; }
    [JsonPropertyName("provider")] public string? Provider { get; set; }
    [JsonPropertyName("adapter")] public string? Adapter { get; set; }
    [JsonPropertyName("extraPrompt")] public string? ExtraPrompt { get; set; }
    [JsonPropertyName("reasoning")] public string? Reasoning { get; set; }
    [JsonPropertyName("batchSize")] public int? BatchSize { get; set; }
    [JsonPropertyName("maxInputCharacters")] public int? MaxInputCharacters { get; set; }
    [JsonPropertyName("maxOutputTokens")] public int? MaxOutputTokens { get; set; }
    [JsonPropertyName("retryCount")] public int? RetryCount { get; set; }
    [JsonPropertyName("timeoutSeconds")] public int? TimeoutSeconds { get; set; }
    [JsonPropertyName("maxReportedTokens")] public int? MaxReportedTokens { get; set; }
    [JsonPropertyName("maxRequests")] public int? MaxRequests { get; set; }
    [JsonPropertyName("jsonOutput")] public bool? JsonOutput { get; set; }
}

public sealed class MachineTaskOptions
{
    [JsonPropertyName("endpoint")] public string? Endpoint { get; set; }
    [JsonPropertyName("provider")] public string? Provider { get; set; }
    [JsonPropertyName("region")] public string? Region { get; set; }
    [JsonPropertyName("batchSize")] public int? BatchSize { get; set; }
    [JsonPropertyName("timeoutSeconds")] public int? TimeoutSeconds { get; set; }
}

internal static class ServerTaskPolicy
{
    internal static void ValidateConfig(ServerProviderConfig config)
    {
        if (config.Ai == null || config.Machine == null || config.AiProfiles == null || config.AiProfiles.Count > 16)
            throw new InvalidDataException("供应商配置段为空或预设数量超过 16。");
        var ids = new HashSet<string>(StringComparer.Ordinal) { "default" };
        foreach (var profile in config.AiProfiles)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.Id) || profile.Id.StartsWith("builtin:") || profile.Id.Length > 64 || !ids.Add(profile.Id) || profile.Settings == null)
                throw new InvalidDataException("预设 ID 重复或参数无效。");
        }
        if (config.MachineProfiles == null || config.MachineProfiles.Count > 16) throw new InvalidDataException("机器翻译预设数量无效。");
        ids = new(StringComparer.Ordinal) { "default" };
        foreach (var profile in config.MachineProfiles)
            if (profile == null || string.IsNullOrWhiteSpace(profile.Id) || profile.Id.StartsWith("builtin:") || profile.Id.Length > 64 || !ids.Add(profile.Id) || profile.Settings == null)
                throw new InvalidDataException("机器翻译预设 ID 或参数无效。");
        foreach (var ai in config.AiProfiles.Select(profile => profile.Settings).Prepend(config.Ai))
        {
            if (ai.AllowedModels == null || ai.AllowedModels.Count > 32 || ai.ExtraPrompt == null || ai.ExtraPrompt.Length > 2000 || string.IsNullOrWhiteSpace(ai.Model))
                throw new InvalidDataException("模型列表或术语配置无效。");
            if (ai.Adapter is not ("auto" or "generic" or "deepseek" or "openai" or "qwen" or "gemini") ||
                ai.Reasoning is not ("default" or "off" or "low" or "medium" or "high")) throw new InvalidDataException("参数适配器或思考强度无效。");
        }
    }

    internal static void Apply(ServerProviderConfig config, TranslationJobRequest request, bool administrator = false)
    {
        if (request.SourceGroups.Count > 500 || request.EntryIds.Count > 10000 ||
            request.SourceGroups.Any(value => value == null || value.Length > 256) ||
            request.EntryIds.Any(value => value == null || value.Length > 256)) throw new InvalidOperationException("筛选范围超过限制。");
        ServerProviderCatalog.Select(config, request, administrator);
        if (request.Mode != "ai")
        {
            var machine = request.MachineOptions;
            if (machine == null) return;
            if (machine.Endpoint != null && machine.Endpoint != config.Machine.Endpoint && !administrator ||
                machine.Provider != null && machine.Provider != config.Machine.Provider && !administrator)
                throw new InvalidOperationException("只有管理员可以修改供应商接口。");
            if (machine.Endpoint != null) config.Machine.Endpoint = machine.Endpoint;
            if (machine.Provider != null) config.Machine.Provider = machine.Provider;
            if (machine.Region != null)
            {
                if (machine.Region.Length > 64 || machine.Region.Any(char.IsControl)) throw new InvalidOperationException("区域参数无效。");
                config.Machine.Region = machine.Region;
            }
            if (machine.BatchSize.HasValue) config.Machine.BatchSize = Math.Clamp(machine.BatchSize.Value, 1, 50);
            if (machine.TimeoutSeconds.HasValue) config.Machine.TimeoutSeconds = Math.Clamp(machine.TimeoutSeconds.Value, 10, 600);
            return;
        }
        var options = request.Options;
        if (options == null) return;
        if (options.Endpoint != null && options.Endpoint != config.Ai.Endpoint && !administrator ||
            options.Provider != null && options.Provider != config.Ai.Provider && !administrator ||
            options.Adapter != null && options.Adapter != config.Ai.Adapter && !administrator)
            throw new InvalidOperationException("只有管理员可以修改供应商接口或适配器。");
        if (options.Endpoint != null) config.Ai.Endpoint = options.Endpoint;
        if (options.Provider != null) config.Ai.Provider = options.Provider;
        if (options.Adapter != null)
        {
            if (options.Adapter is not ("auto" or "generic" or "deepseek" or "openai" or "qwen" or "gemini")) throw new InvalidOperationException("适配器无效。");
            config.Ai.Adapter = options.Adapter;
        }
        if (options.Model != null && (string.IsNullOrWhiteSpace(options.Model) || options.Model.Length > 256 || options.Model.Any(char.IsControl)))
            throw new InvalidOperationException("模型 ID 无效。");
        if (!administrator && options.Model != null && options.Model != config.Ai.Model && !config.Ai.AllowedModels.Contains(options.Model, StringComparer.Ordinal))
            throw new InvalidOperationException("此模型不在管理员批准的预设列表中。");
        if (options.Model != null) config.Ai.Model = options.Model;
        if (options.ExtraPrompt != null)
        {
            if (options.ExtraPrompt.Length > 2000) throw new InvalidOperationException("附加术语最多 2000 字符。");
            config.Ai.ExtraPrompt += "\n" + options.ExtraPrompt;
        }
        if (options.Reasoning != null)
        {
            if (options.Reasoning is not ("default" or "off" or "low" or "medium" or "high")) throw new InvalidOperationException("思考强度无效。");
            config.Ai.Reasoning = options.Reasoning;
        }
        if (options.BatchSize.HasValue) config.Ai.BatchSize = Math.Clamp(options.BatchSize.Value, 1, 60);
        if (options.MaxInputCharacters.HasValue) config.Ai.MaxInputCharacters = Math.Clamp(options.MaxInputCharacters.Value, 1000, 60000);
        if (options.MaxOutputTokens.HasValue) config.Ai.MaxOutputTokens = Math.Clamp(options.MaxOutputTokens.Value, 256, 32768);
        if (options.RetryCount.HasValue) config.Ai.RetryCount = Math.Clamp(options.RetryCount.Value, 0, 3);
        if (options.TimeoutSeconds.HasValue) config.Ai.TimeoutSeconds = Math.Clamp(options.TimeoutSeconds.Value, 10, 600);
        if (options.JsonOutput.HasValue) config.Ai.JsonOutput = options.JsonOutput.Value;
        if (options.MaxReportedTokens.HasValue) config.Ai.MaxReportedTokens = Tighter(config.Ai.MaxReportedTokens, options.MaxReportedTokens.Value);
        if (options.MaxRequests.HasValue) config.Ai.MaxRequests = Tighter(config.Ai.MaxRequests, options.MaxRequests.Value);
    }

    private static int Tighter(int configured, int requested) => configured > 0
        ? requested > 0 ? Math.Min(configured, requested) : configured : Math.Max(0, requested);
}
