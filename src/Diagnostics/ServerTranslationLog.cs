using System.Text;
using System.Text.Json;

namespace SimpleTranslatorCS;

internal static class ServerTranslationLog
{
    private static readonly object Sync = new();
    internal static string Root => TranslationFoundation.TranslationLogPath.ForProject(TranslationWorkbench.ModPath, "SimpleTranslatorCS");
    internal static void Write(string action, string identity, string state, string detail = "")
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Root);
                var record = JsonSerializer.Serialize(new { timeUtc = DateTimeOffset.UtcNow, action, identity, state, detail },
                    new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                File.AppendAllText(Path.Combine(Root, DateTime.UtcNow.ToString("yyyyMMdd") + ".operations.jsonl"), record + "\n", new UTF8Encoding(false));
                if (state is "失败" or "异常" or "拒绝" or "部分完成")
                    File.AppendAllText(Path.Combine(Root, DateTime.UtcNow.ToString("yyyyMMdd") + ".errors.jsonl"), record + "\n", new UTF8Encoding(false));
            }
        }
        catch { /* Diagnostics must not break server requests. */ }
    }
}
