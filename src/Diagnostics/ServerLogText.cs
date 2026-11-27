using System.Net;
using System.Text;
using System.Text.Json;

namespace SimpleTranslatorCS;

internal static class ServerLogText
{
    internal static string Mode(string? mode) => mode switch
    {
        "ai" => "AI 翻译",
        "machine" => "机器翻译",
        "manual" => "人工译文",
        _ => "未知模式"
    };

    internal static string Redact(string value, params string?[] credentials)
    {
        foreach (var credential in credentials.OfType<string>().Where(value => !string.IsNullOrWhiteSpace(value))
                     .SelectMany(value => new[] { value, value.Trim() }).Distinct().OrderByDescending(value => value.Length))
            value = value.Replace(credential, "[已隐藏]", StringComparison.Ordinal);
        return value;
    }

    internal static string YesNo(bool value) => value ? "是" : "否";

    internal static string Domain(string domain) => domain switch
    {
        "item" => "物品",
        "quest" => "任务",
        "achievement" => "成就",
        "trader" => "商人",
        "customization" => "角色外观",
        "handbook" => "手册",
        "locale" => "其他语言表文本",
        _ => "其他词条"
    };

    internal static string JobState(string state) => state switch
    {
        "running" or "in_progress" => "进行中",
        "not_started" => "未开始",
        "completed" => "已完成",
        "partial" => "部分完成",
        "failed" => "失败",
        "cancelled" => "已取消",
        "interrupted" => "已中断",
        "saved" => "已保存",
        "queued" => "待审核",
        "skipped" => "已跳过",
        _ => "未知状态"
    };

    internal static string HttpFailure(HttpStatusCode status)
    {
        var hint = (int)status switch
        {
            400 => "请检查模型和请求参数。",
            401 or 403 => "请检查 API 密钥、模型权限和账号状态。",
            404 => "请检查服务端接口地址及模型是否存在。",
            413 => "请求体过大，请缩小批次或输入字符预算。",
            429 => "请求过于频繁或额度不足，请检查供应商限额。",
            >= 500 => "供应商服务暂时异常，请稍后重试。",
            _ => "请检查服务端接口地址和供应商状态。"
        };
        return $"翻译接口返回 HTTP {(int)status}。{hint}";
    }

    // Raw framework/provider exception messages can contain request data, URLs or credentials.
    internal static string Failure(Exception error) => error switch
    {
        JsonException json => JsonFailure(json),
        DecoderFallbackException => "文件编码无效，请另存为 UTF-8。",
        UnauthorizedAccessException => "文件访问被拒绝，请检查服务端账号的目录与文件权限。",
        FileNotFoundException => "文件不存在，请确认路径和文件名。",
        DirectoryNotFoundException => "目录不存在，请确认安装位置和目录结构。",
        HttpRequestException => "连接翻译接口失败，请检查服务端网络、TLS 和接口地址。",
        TimeoutException => "请求翻译接口超时，请检查服务端网络或超时设置。",
        InvalidDataException data when data.TargetSite?.DeclaringType == typeof(TranslationWorkbench) => data.Message,
        InvalidDataException => "文件或接口数据无效，请检查字段内容与格式。",
        IOException io when io.TargetSite?.DeclaringType == typeof(TranslationStorage) && io.InnerException != null => io.Message,
        IOException => "文件读写失败，请检查文件是否被占用、磁盘空间及目录权限。",
        KeyNotFoundException => "缺少预期的数据字段，请核对文件格式或接口返回格式。",
        InvalidOperationException => "当前数据状态不符合操作要求，请检查配置和数据格式。",
        _ => $"发生未预期的错误（{error.GetType().Name}）。"
    };

    private static string JsonFailure(JsonException error)
    {
        var location = error.LineNumber is long line
            ? $"（行号 {line + 1}，行内字节偏移 {error.BytePositionInLine ?? 0}）"
            : "";
        return $"JSON/JSONC 格式无效{location}，请检查括号、引号、逗号和字段类型。";
    }
}
