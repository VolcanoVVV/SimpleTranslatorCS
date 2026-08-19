using System.Text.Json;

namespace SimpleTranslatorCS;

/// <summary>
/// mod 目录下 config.jsonc 的运行时配置。
/// 文件不存在或解析失败时返回默认（全部关闭），不影响正常加载。
/// </summary>
internal sealed record ModConfig(
    bool DevMode,
    string ReportFile)
{
    public const string DefaultReportFile = "translation-report.jsonc";

    public static ModConfig Default => new(false, DefaultReportFile);

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

            return new ModConfig(
                devMode,
                string.IsNullOrWhiteSpace(reportFile) ? DefaultReportFile : reportFile!);
        }
        catch (Exception ex)
        {
            warn?.Invoke($"config.jsonc 解析失败，开发模式已跳过: {ex.Message}");
            return Default;
        }
    }
}
