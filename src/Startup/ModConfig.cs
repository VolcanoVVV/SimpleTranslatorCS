using System.Text.Json;

namespace SimpleTranslatorCS;

/// <summary>
/// mod 目录下 config.jsonc 的运行时配置。
/// 文件不存在或解析失败时返回默认（全部关闭），不影响正常加载。
/// </summary>
internal sealed record ModConfig(
    bool DevMode,
    string ReportFile,
    string WorkbenchAdminKey,
    int PullCooldownSeconds = 180,
    bool IncludeOtherLocaleText = false)
{
    public const string DefaultReportFile = "cache/reports/translation-report.jsonc";

    public static ModConfig Default => new(false, DefaultReportFile, string.Empty);

    public static ModConfig Load(string modPath, Action<string>? warn)
    {
        var configPath = Path.Combine(modPath, "config.jsonc");
        if (!File.Exists(configPath))
        {
            return Default;
        }

        try
        {
            using var doc = JsonDocument.Parse(Jsonc.Normalize(File.ReadAllText(configPath)));
            var root = doc.RootElement;

            var devMode = root.TryGetProperty("devMode", out var dm) && dm.ValueKind == JsonValueKind.True;
            var reportFile = root.TryGetProperty("reportFile", out var rf) && rf.ValueKind == JsonValueKind.String
                ? rf.GetString()
                : null;

            var adminKey = root.TryGetProperty("workbenchAdminKey", out var ak) && ak.ValueKind == JsonValueKind.String
                ? ak.GetString() ?? string.Empty
                : string.Empty;

            return new ModConfig(devMode,
                string.IsNullOrWhiteSpace(reportFile) || reportFile == "translation-report.jsonc" ? DefaultReportFile : reportFile!,
                adminKey.Trim(),
                root.TryGetProperty("pullCooldownSeconds", out var cooldown) && cooldown.ValueKind == JsonValueKind.Number && cooldown.TryGetInt32(out var seconds)
                    && seconds >= 0 && seconds <= 86400 ? seconds : 180,
                root.TryGetProperty("includeOtherLocaleText", out var other) && other.ValueKind == JsonValueKind.True);
        }
        catch (Exception ex)
        {
            warn?.Invoke($"无法读取 config.jsonc，已使用默认配置。{ServerLogText.Failure(ex)}");
            return Default;
        }
    }
}
