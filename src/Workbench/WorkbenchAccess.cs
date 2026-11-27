using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SimpleTranslatorCS;

internal static class WorkbenchAccess
{
    public const string KeyFileName = "workbench-admin.jsonc";

    public static bool DetectFika(string modPath)
    {
        var modsRoot = Directory.GetParent(modPath)?.FullName;
        if (string.IsNullOrWhiteSpace(modsRoot) || !Directory.Exists(modsRoot))
            return false;

        foreach (var directory in Directory.EnumerateDirectories(modsRoot))
        {
            var name = Path.GetFileName(directory);
            if (name.Contains("fika", StringComparison.OrdinalIgnoreCase))
                return true;
            foreach (var manifest in new[] { "package.json", "mod.json" })
            {
                var path = Path.Combine(directory, manifest);
                if (!File.Exists(path))
                    continue;
                try
                {
                    var text = File.ReadAllText(path);
                    if (text.Contains("project-fika", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("fika-server", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // An unreadable unrelated manifest must not stop the translator from loading.
                }
            }
        }
        return false;
    }

    public static string EnsureKey(string modPath, string? configuredKey)
    {
        var path = Path.Combine(modPath, KeyFileName);
        if (File.Exists(path))
        {
            using var document = JsonDocument.Parse(Jsonc.Normalize(File.ReadAllText(path)));
            if (!document.RootElement.TryGetProperty("adminKey", out var value)
                || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new InvalidOperationException(KeyFileName + " 中的 adminKey 为空或无效；请修复文件，或删除后重启以重新生成。");
            return value.GetString()!.Trim();
        }
        // Migrate the legacy value without editing arbitrary user JSONC or its comments.
        var key = !string.IsNullOrWhiteSpace(configuredKey) ? configuredKey.Trim()
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var text = "{\n" +
                   "    // 服务端汉化管理员密钥：允许推送译文、修改文件开关、更新基准和重新加载磁盘译文。\n" +
                   "    // 普通玩家无需此密钥，只能按 config.jsonc 中的冷却时间手动拉取已生效文本。\n" +
                   "    // 将 adminKey 填入客户端工作台的管理员密钥框；仅交给可信管理员，不要公开此文件。\n" +
                   "    // 修改后重启服务端生效；删除此文件后重启会生成新密钥（旧 config 中仍有密钥则优先迁移）。\n" +
                   "    \"adminKey\": " + JsonSerializer.Serialize(key) + "\n}\n";
        AtomicWrite(path, text);
        return key;
    }

    public static bool FixedEquals(string expected, string? supplied)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(supplied))
            return false;
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
}
