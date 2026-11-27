using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimpleTranslatorCS;

// Startup-only migration. Translation files retain their original bytes/comments.
internal static class TranslationStorage
{
    internal static void Prepare(string root)
    {
        var oldWorkbench = Path.Combine(root, "workbench");
        var migrateLegacySelection = Directory.Exists(oldWorkbench) && Directory.EnumerateFiles(oldWorkbench, "*", SearchOption.AllDirectories).Any();
        MoveTree(root, Path.Combine(root, "workbench"), Path.Combine(root, "cache", "workbench"));
        MoveTree(root, Path.Combine(root, "cache", "workbench", "cache"), Path.Combine(root, "cache", "workbench", "records"));
        MoveTree(root, Path.Combine(root, "data", "translation-diagnostics"), Path.Combine(root, "cache", "diagnostics"));
        MoveFile(root, Path.Combine(root, "data", "pending-contributions.json"), Path.Combine(root, "cache", "contributions", "pending-contributions.json"));
        MoveFile(root, Path.Combine(root, "translation-report.jsonc"), Path.Combine(root, ModConfig.DefaultReportFile));
        // Legacy translations are read in place, including files added after an upgrade.
        // Never create, move, overwrite or delete db/locales on behalf of the user.
        Directory.CreateDirectory(Path.Combine(root, "translations", "ch"));
        var legacy = Path.Combine(root, "db", "locales", "ch");
        var mappings = (Directory.Exists(legacy) ? Directory.EnumerateFiles(legacy, "*", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(file => Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                               Path.GetExtension(file).Equals(".jsonc", StringComparison.OrdinalIgnoreCase)) : [])
            .ToDictionary(file => Path.GetRelativePath(legacy, file).Replace('\\', '/'),
                file => "@legacy/" + Path.GetRelativePath(legacy, file).Replace('\\', '/'), StringComparer.Ordinal);
        if (migrateLegacySelection && mappings.Count > 0) RewriteManualPaths(root, mappings);
    }

    // Stage the whole batch before replacing any live file. Each replace is atomic; if a later
    // replacement fails, restore previously replaced files from their exact original bytes.
    internal static void WriteBatch(IReadOnlyDictionary<string, string> files)
    {
        var staged = new List<(string Path, string Draft, string? Backup)>();
        var replaced = 0;
        var rollbackFailed = false;
        try
        {
            foreach (var (path, content) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var prefix = path + "." + Guid.NewGuid().ToString("N");
                var backup = File.Exists(path) ? prefix + ".bak" : null;
                var draft = prefix + ".tmp";
                staged.Add((path, draft, backup));
                if (backup != null) File.Copy(path, backup);
                File.WriteAllText(draft, content, new UTF8Encoding(false));
            }
            foreach (var file in staged)
            {
                File.Move(file.Draft, file.Path, file.Backup != null);
                replaced++;
            }
        }
        catch (Exception original)
        {
            for (var index = replaced - 1; index >= 0; index--)
            {
                var file = staged[index];
                try
                {
                    if (file.Backup == null) File.Delete(file.Path);
                    else File.Move(file.Backup, file.Path, true);
                }
                catch { rollbackFailed = true; }
            }
            if (rollbackFailed)
                throw new IOException("译文批次写入失败，部分文件未能还原。请保留并检查译文目录中的 .bak 文件。", original);
            throw;
        }
        finally
        {
            foreach (var file in staged)
            {
                TryDelete(file.Draft);
                if (!rollbackFailed && file.Backup != null) TryDelete(file.Backup);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void RewriteManualPaths(string root, Dictionary<string, string> mappings)
    {
        var workbench = Path.Combine(root, "cache", "workbench");
        foreach (var name in new[] { "manual-files.jsonc", "baselines/manual.jsonc" })
        {
            var path = Path.Combine(workbench, name);
            if (!File.Exists(path)) continue;
            var document = JsonNode.Parse(Jsonc.Normalize(File.ReadAllText(path)))!.AsObject();
            if (document["disabled"] is JsonArray disabled)
                document["disabled"] = new JsonArray(disabled.Select(value => value == null ? null :
                    JsonValue.Create(mappings.GetValueOrDefault(value.GetValue<string>(), value.GetValue<string>()))).ToArray());
            if (document["files"] is JsonObject baseline)
            {
                var updated = new JsonObject();
                foreach (var pair in baseline) updated[mappings.GetValueOrDefault(pair.Key, pair.Key)] = pair.Value?.DeepClone();
                document["files"] = updated;
            }
            var backup = Path.Combine(root, "cache", "migration", "metadata", name);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            if (!File.Exists(backup)) File.Copy(path, backup);
            File.WriteAllText(path + ".tmp", document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, true);
        }
    }

    private static void MoveTree(string root, string source, string target)
    {
        if (!Directory.Exists(source)) return;
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            MoveFile(root, file, Path.Combine(target, Path.GetRelativePath(source, file)));
    }

    private static void MoveFile(string root, string source, string target)
    {
        if (!File.Exists(source)) return;
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(source).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(target).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("迁移路径越界。");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            if (!File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(target)))
                throw new IOException("迁移发现不同内容的同名文件，请手动核对：" + target);
            // Preserve duplicate originals too; moving never deletes user data.
            target = Path.Combine(root, "cache", "migration", Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        }
        File.Move(source, target);
    }
}
