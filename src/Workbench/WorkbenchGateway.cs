using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SimpleTranslatorCS;

// Session IDs identify an SPT profile; only the separate administrator secret grants writes.
internal static class WorkbenchGateway
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, (DateTimeOffset Start, int Count)> Windows = new(StringComparer.Ordinal);
    private static DateTimeOffset _globalStart;
    private static int _globalCount;
    internal static string Identity(string session) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session)))[..12];
    internal static string? Check(string session, bool knownProfile, bool admin, WorkbenchAdminRequest? request, bool requireAdmin = false)
    {
        if (request == null || (request.AdminKey?.Length ?? 0) > 256) return Error("请求无效。");
        if (requireAdmin && !admin) return Error("Fika 工作台需要服务端管理员密钥。", true);
        if (!admin && (!knownProfile || string.IsNullOrWhiteSpace(session))) return Error("需要服务端已有的玩家档案。", true);
        if (request is WorkbenchSaveRequest save && (save.Entries == null || save.Entries.Count > 1000 ||
            save.Entries.Sum(entry => (long)(entry?.Source?.Length ?? 0) + (entry?.Translation?.Length ?? 0)) > 500000))
            return Error("批量回填超过 1000 条或 500000 字符。");
        lock (Sync)
        {
            var now = TranslationWorkbench.UtcNow();
            foreach (var key in Windows.Where(pair => now - pair.Value.Start >= TimeSpan.FromMinutes(1)).Select(pair => pair.Key).ToArray()) Windows.Remove(key);
            if (now - _globalStart >= TimeSpan.FromMinutes(1)) { _globalStart = now; _globalCount = 0; }
            if (_globalCount >= 600) return Error("服务繁忙，请稍后重试。", rate: true);
            var identity = admin ? "admin" : Identity(session);
            if (!Windows.TryGetValue(identity, out var window)) window = (now, 0);
            if (window.Count >= 120) return Error("请求过于频繁，请稍后重试。", rate: true);
            Windows[identity] = (window.Start, window.Count + 1);
            _globalCount++;
        }
        return null;
    }
    private static string Error(string message, bool unauthorized = false, bool rate = false) =>
        JsonSerializer.Serialize(new { ok = false, unauthorized, rateLimited = rate, error = message });
}
