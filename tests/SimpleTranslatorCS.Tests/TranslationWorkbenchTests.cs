using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SimpleTranslatorCS.Tests;

[TestClass]
public class TranslationWorkbenchTests
{
    private const string Item = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Quest = "bbbbbbbbbbbbbbbbbbbbbbbb";

    [TestMethod]
    public void Catalog_ClassifiesStrictMissingAndExcludesGeneratedFiles()
    {
        var root = CreateRoot();
        try
        {
            var locale = Path.Combine(root, "db", "locales", "ch");
            Directory.CreateDirectory(Path.Combine(locale, "_workbench"));
            File.WriteAllText(Path.Combine(locale, "manual.jsonc"), "{\"" + Item + " Name\":\"人工\"}");
            File.WriteAllText(Path.Combine(locale, "_workbench", "20-ai.jsonc"), "{}");
            var english = new Dictionary<string, string>
            {
                [Item + " Name"] = "Test item",
                [Item + " ShortName"] = "Test",
                [Quest + " name"] = "A quest",
                ["cccccccccccccccccccccccc"] = "Find the item",
                ["unrelated"] = "Not in a supported domain"
            };
            var chinese = new Dictionary<string, string>
            {
                [Item + " Name"] = "测试物品",
                [Item + " ShortName"] = "Test",
                [Quest + " name"] = ""
            };
            Initialize(root, english, chinese, new[] { Item },
                new[] { Quest, Quest + " name", "cccccccccccccccccccccccc" });

            using var json = JsonDocument.Parse(TranslationWorkbench.BuildCatalogJson());
            var entries = json.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            Assert.AreEqual(3, entries.Length);
            Assert.AreEqual("base", entries.Single(entry => entry.GetProperty("id").GetString() == Item + " ShortName")
                .GetProperty("origin").GetString());
            Assert.AreEqual("base", entries.Single(entry => entry.GetProperty("id").GetString() == "cccccccccccccccccccccccc")
                .GetProperty("origin").GetString());
            var files = json.RootElement.GetProperty("files").EnumerateArray().ToArray();
            Assert.AreEqual(1, files.Count(file => file.GetProperty("kind").GetString() == "manual"));
            Assert.AreEqual("@legacy/manual.jsonc", files.Single(file => file.GetProperty("kind").GetString() == "manual")
                .GetProperty("path").GetString());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void LoadingOrder_AlwaysPlacesAutomaticLayersBeforeManualFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "simpletranslator-order");
        var files = new[]
        {
            Path.Combine(root, "A-manual.jsonc"),
            Path.Combine(root, "示例_API.jsonc"),
            Path.Combine(root, "API", "generated.jsonc"),
            Path.Combine(root, "_workbench", "20-ai.jsonc"),
            Path.Combine(root, "z-manual.jsonc"),
            Path.Combine(root, "_workbench", "10-machine.jsonc")
        };
        var ordered = TranslationWorkbench.OrderForLoading(root, files)
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/')).ToArray();
        CollectionAssert.AreEqual(new[]
        {
            "_workbench/10-machine.jsonc", "_workbench/20-ai.jsonc", "API/generated.jsonc",
            "示例_API.jsonc", "A-manual.jsonc", "z-manual.jsonc"
        }, ordered);
    }

    [TestMethod]
    public void FileSelectionAndBaseline_ArePersistentAndReportChanges()
    {
        var root = CreateRoot();
        try
        {
            var locale = Path.Combine(root, "db", "locales", "ch");
            Directory.CreateDirectory(locale);
            var manual = Path.Combine(locale, "manual.jsonc");
            File.WriteAllText(manual, "{\"" + Item + " Name\":\"人工\"}");
            var english = new Dictionary<string, string> { [Item + " Name"] = "Before" };
            Initialize(root, english, new Dictionary<string, string>(), new[] { Item }, Array.Empty<string>());

            var disabled = TranslationWorkbench.SetFileEnabledJson(new WorkbenchFileSelectionRequest { AdminKey = "secret", Path = "@legacy/manual.jsonc", Enabled = false });
            StringAssert.Contains(disabled, "\"restartRequired\": false");
            Assert.IsFalse(TranslationWorkbench.IsManualFileEnabled(root, locale, manual));
            TranslationWorkbench.UpdateBaselineJson(new WorkbenchAdminRequest { AdminKey = "secret" });
            english[Item + " Name"] = "After";

            using var json = JsonDocument.Parse(TranslationWorkbench.BuildCatalogJson());
            var file = json.RootElement.GetProperty("files")[0];
            Assert.IsFalse(file.GetProperty("enabled").GetBoolean());
            Assert.IsTrue(file.GetProperty("baselineKnown").GetBoolean());
            Assert.AreEqual(1, file.GetProperty("changedCount").GetInt32());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void SaveBatch_IsAtomicAgainstRevisionAndWritesCacheAndActiveLayer()
    {
        var root = CreateRoot();
        try
        {
            var locale = Path.Combine(root, "db", "locales", "ch");
            Directory.CreateDirectory(locale);
            var key = Item + " Description";
            var english = new Dictionary<string, string> { [key] = "A useful item" };
            var chinese = new Dictionary<string, string>();
            Initialize(root, english, chinese, new[] { Item }, Array.Empty<string>());
            using var catalog = JsonDocument.Parse(TranslationWorkbench.BuildCatalogJson());
            var revision = catalog.RootElement.GetProperty("revision").GetString()!;

            var stale = TranslationWorkbench.SaveBatchJson(new WorkbenchSaveRequest { AdminKey = "secret", Mode = "ai", Revision = "old", Entries = [new WorkbenchSaveEntry { Id = key, Source = english[key], Translation = "物品" }]
            });
            using (var staleJson = JsonDocument.Parse(stale))
                Assert.IsTrue(staleJson.RootElement.GetProperty("conflict").GetBoolean());
            Assert.IsFalse(File.Exists(Path.Combine(TranslationWorkbench.LocaleRoot(root), "原版游戏_API.jsonc")));

            var saved = TranslationWorkbench.SaveBatchJson(new WorkbenchSaveRequest { AdminKey = "secret", Mode = "ai", Revision = revision, Profile = "profile",
                Entries = [new WorkbenchSaveEntry { Id = key, Source = english[key], Translation = "有用的物品" }]
            });
            using (var savedJson = JsonDocument.Parse(saved))
                Assert.AreEqual(1, savedJson.RootElement.GetProperty("saved").GetInt32());
            Assert.AreEqual("有用的物品", chinese[key]);
            StringAssert.Contains(File.ReadAllText(Path.Combine(TranslationWorkbench.LocaleRoot(root), "原版游戏_API.jsonc")), "有用的物品");
            StringAssert.Contains(File.ReadAllText(Path.Combine(root, "cache", "workbench", "records", "API", "records.jsonc")), "A useful item");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void ReviewSave_RejectsChangedSourceAndDamagedMarkup_ButAppliesValidCorrection()
    {
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "db", "locales", "ch"));
            var key = Item + " Description";
            var english = new Dictionary<string, string> { [key] = "Use <b>2</b> times" };
            var chinese = new Dictionary<string, string>();
            Initialize(root, english, chinese, new[] { Item }, Array.Empty<string>());
            using var catalog = JsonDocument.Parse(TranslationWorkbench.BuildCatalogJson());
            var revision = catalog.RootElement.GetProperty("revision").GetString()!;
            TranslationWorkbench.SaveBatchJson(new WorkbenchSaveRequest { AdminKey = "secret", Mode = "ai",
                Revision = revision, Entries = [new WorkbenchSaveEntry { Id = key, Source = english[key],
                    Translation = "使用 <b>2</b> 次" }] });

            using var review = JsonDocument.Parse(TranslationWorkbench.ReviewJson(new WorkbenchAdminRequest { AdminKey = "secret" }));
            Assert.AreEqual(key, review.RootElement.GetProperty("entries")[0].GetProperty("id").GetString());
            var bad = TranslationWorkbench.ReviewSaveJson(new WorkbenchReviewRequest { AdminKey = "secret", Mode = "ai",
                Id = key, Source = english[key], Translation = "使用 2 次" });
            using (var result = JsonDocument.Parse(bad)) Assert.IsFalse(result.RootElement.GetProperty("ok").GetBoolean());
            Assert.AreEqual("使用 <b>2</b> 次", chinese[key]);
            var changed = TranslationWorkbench.ReviewSaveJson(new WorkbenchReviewRequest { AdminKey = "secret", Mode = "ai",
                Id = key, Source = "old source", Translation = "使用 <b>2</b> 次" });
            using (var result = JsonDocument.Parse(changed)) Assert.IsTrue(result.RootElement.GetProperty("conflict").GetBoolean());
            var saved = TranslationWorkbench.ReviewSaveJson(new WorkbenchReviewRequest { AdminKey = "secret", Mode = "ai",
                Id = key, Source = english[key], Translation = "可使用 <b>2</b> 次" });
            using (var result = JsonDocument.Parse(saved)) Assert.IsTrue(result.RootElement.GetProperty("ok").GetBoolean());
            Assert.AreEqual("可使用 <b>2</b> 次", chinese[key]);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void Catalog_GroupsEntriesBySiblingModFolder()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "simpletranslator-sources-" + Guid.NewGuid().ToString("N"));
        var modsRoot = Path.Combine(testRoot, "mods");
        var root = Path.Combine(modsRoot, "SimpleTranslatorCS");
        var sourceMod = Path.Combine(modsRoot, "Example-Weapon-Pack");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sourceMod);
        File.WriteAllText(Path.Combine(sourceMod, "items.json"), "{\"" + Item + "\":{\"_id\":\"" + Item + "\"}}");
        try
        {
            Initialize(root,
                new Dictionary<string, string> { [Item + " Name"] = "Example item" },
                new Dictionary<string, string>(), new[] { Item }, Array.Empty<string>());
            using var catalog = JsonDocument.Parse(TranslationWorkbench.BuildCatalogJson());
            var entry = catalog.RootElement.GetProperty("entries")[0];
            Assert.AreEqual("Example-Weapon-Pack", entry.GetProperty("sourceGroup").GetString());
            Assert.AreEqual("mod", entry.GetProperty("origin").GetString());
            Assert.AreEqual(1, catalog.RootElement.GetProperty("sourceGroups")[0].GetProperty("entryCount").GetInt32());
        }
        finally
        {
            Directory.Delete(testRoot, true);
        }
    }

    [TestMethod]
    public void CatalogRefresh_RescansSourcesAndPrefersDefinitionsOverRepeatedReferences()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "simpletranslator-refresh-" + Guid.NewGuid().ToString("N"));
        var modsRoot = Path.Combine(testRoot, "mods");
        var root = Path.Combine(modsRoot, "SimpleTranslatorCS");
        var referenceMod = Path.Combine(modsRoot, "A-Reference-Pack");
        var ownerMod = Path.Combine(modsRoot, "Z-Owner-Pack");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(referenceMod);
        Directory.CreateDirectory(ownerMod);
        File.WriteAllText(Path.Combine(referenceMod, "references.json"),
            "[" + string.Join(',', Enumerable.Repeat("\"" + Item + "\"", 20)) + "]");
        try
        {
            Initialize(root,
                new Dictionary<string, string> { [Item + " Name"] = "Example item" },
                new Dictionary<string, string>(), new[] { Item }, Array.Empty<string>());
            using (var first = JsonDocument.Parse(TranslationWorkbench.BuildCatalogJson()))
                Assert.AreEqual("A-Reference-Pack", first.RootElement.GetProperty("entries")[0]
                    .GetProperty("sourceGroup").GetString());

            File.WriteAllText(Path.Combine(ownerMod, "items.json"),
                "{\"" + Item + "\":{\"_id\":\"" + Item + "\"}}");
            using var refreshed = JsonDocument.Parse(TranslationWorkbench.ReloadJson(new WorkbenchAdminRequest { AdminKey = "secret" }));
            Assert.AreEqual("Z-Owner-Pack", refreshed.RootElement.GetProperty("catalog").GetProperty("entries")[0]
                .GetProperty("sourceGroup").GetString());
        }
        finally
        {
            Directory.Delete(testRoot, true);
        }
    }

    [TestMethod]
    public void FikaAuth_RejectsWrongKeyAndAllowsCorrectKey()
    {
        var root = CreateRoot();
        try
        {
            var locale = Path.Combine(root, "db", "locales", "ch");
            Directory.CreateDirectory(locale);
            var key = Item + " Name";
            var english = new Dictionary<string, string> { [key] = "Item" };
            Initialize(root, english, new Dictionary<string, string>(), new[] { Item }, Array.Empty<string>(), true, "secret");
            using var catalog = JsonDocument.Parse(TranslationWorkbench.BuildCatalogJson());
            var revision = catalog.RootElement.GetProperty("revision").GetString()!;
            var denied = TranslationWorkbench.SaveBatchJson(new WorkbenchSaveRequest
            {
                AdminKey = "wrong", Mode = "ai", Revision = revision,
                Entries = [new WorkbenchSaveEntry { Id = key, Source = "Item", Translation = "物品" }]
            });
            Assert.IsTrue(JsonDocument.Parse(denied).RootElement.GetProperty("unauthorized").GetBoolean());
            Assert.IsFalse(File.Exists(Path.Combine(TranslationWorkbench.LocaleRoot(root), "原版游戏_API.jsonc")));

            var accepted = TranslationWorkbench.SaveBatchJson(new WorkbenchSaveRequest
            {
                AdminKey = "secret", Mode = "ai", Revision = revision,
                Entries = [new WorkbenchSaveEntry { Id = key, Source = "Item", Translation = "物品" }]
            });
            Assert.IsTrue(JsonDocument.Parse(accepted).RootElement.GetProperty("ok").GetBoolean());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void Reload_RebuildsEnabledFilesAndRestoresDisabledKeys()
    {
        var root = CreateRoot();
        try
        {
            var locale = Path.Combine(root, "db", "locales", "ch");
            Directory.CreateDirectory(locale);
            var translation = Path.Combine(locale, "manual.jsonc");
            var key = Item + " Name";
            File.WriteAllText(translation, "{\"" + key + "\":\"人工物品\"}");
            var chinese = new Dictionary<string, string> { [key] = "原版中文" };
            Initialize(root, new Dictionary<string, string> { [key] = "Item" }, chinese,
                new[] { Item }, Array.Empty<string>());
            TranslationWorkbench.CaptureBaseTranslations(chinese, new[] { key });
            var reloaded = TranslationWorkbench.ReloadJson(new WorkbenchAdminRequest { AdminKey = "secret" });
            Assert.IsTrue(JsonDocument.Parse(reloaded).RootElement.GetProperty("liveApplied").GetBoolean());
            Assert.AreEqual("人工物品", chinese[key]);

            TranslationWorkbench.SetFileEnabledJson(new WorkbenchFileSelectionRequest { AdminKey = "secret", Path = "@legacy/manual.jsonc", Enabled = false });
            Assert.AreEqual("原版中文", chinese[key]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void DialogueFileChanges_RequireRestartAndDoNotClaimLiveApplication()
    {
        var root = CreateRoot();
        try
        {
            var locale = Path.Combine(root, "db", "locales", "ch");
            var dialogue = Path.Combine(locale, "dialogue", "trader.jsonc");
            Directory.CreateDirectory(Path.GetDirectoryName(dialogue)!);
            File.WriteAllText(dialogue, "{\"line\":\"初始对白\"}");
            var chinese = new Dictionary<string, string>();
            Initialize(root, new Dictionary<string, string>(), chinese,
                Array.Empty<string>(), Array.Empty<string>());

            var toggled = TranslationWorkbench.SetFileEnabledJson(new WorkbenchFileSelectionRequest { AdminKey = "secret", Path = "@legacy/dialogue/trader.jsonc", Enabled = false });
            Assert.IsTrue(JsonDocument.Parse(toggled).RootElement.GetProperty("restartRequired").GetBoolean());

            var reloaded = TranslationWorkbench.ReloadJson(new WorkbenchAdminRequest { AdminKey = "secret" });
            Assert.IsTrue(JsonDocument.Parse(reloaded).RootElement.GetProperty("restartRequired").GetBoolean());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [DataRow(false)]
    [DataRow(true)]
    [TestMethod]
    public void BatchSourcesAndFlatFilesKeepTheSamePriorityOnReload(bool combine)
    {
        var fixture = CreateRoot();
        var mods = Path.Combine(fixture, "user", "mods");
        var root = Path.Combine(mods, "Volcano-SimpleTranslator");
        var owner = Path.Combine(mods, "示例模组");
        Directory.CreateDirectory(owner);
        const string originalKey = Item + " Name";
        const string ownedKey = Quest + " Name";
        try
        {
            File.WriteAllText(Path.Combine(owner, "items.json"), JsonSerializer.Serialize(new Dictionary<string, string> { [Quest] = "Mod item" }));
            TranslationWorkbench.PrepareStorage(root);
            var locale = TranslationWorkbench.LocaleRoot(root);
            Assert.IsFalse(Directory.Exists(Path.Combine(locale, "API")));
            var english = new Dictionary<string, string> { [originalKey] = "Base item", [ownedKey] = "Mod item" };
            var chinese = new Dictionary<string, string>();
            Initialize(root, english, chinese, [Item, Quest], []);
            using var saved = JsonDocument.Parse(TranslationWorkbench.SaveBatchJson(new()
            {
                AdminKey = "secret", Mode = "ai", CombineSources = combine, Revision = TranslationWorkbench.JobCatalog().Revision,
                Entries =
                [
                    new() { Id = originalKey, Source = english[originalKey], Translation = "原版译文" },
                    new() { Id = ownedKey, Source = english[ownedKey], Translation = "模组译文" }
                ]
            }));
            Assert.IsTrue(saved.RootElement.GetProperty("ok").GetBoolean());
            const string combinedName = "原版游戏.示例模组_API.jsonc";
            CollectionAssert.AreEquivalent(combine ? new[] { combinedName } : new[] { "原版游戏_API.jsonc", "示例模组_API.jsonc" },
                saved.RootElement.GetProperty("activeFiles").EnumerateArray().Select(file => file.GetString()).ToArray());
            var modOutput = Jsonc.ParseFile(Path.Combine(locale, combine ? combinedName : "示例模组_API.jsonc"), null)!;
            Assert.AreEqual(combine ? 2 : 1, modOutput.Count);
            Assert.AreEqual("模组译文", modOutput[ownedKey]);

            var nested = Path.Combine(locale, "Manual", "old.jsonc");
            Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
            File.WriteAllText(nested, JsonSerializer.Serialize(new Dictionary<string, string> { [ownedKey] = "旧人工译文" }));
            var flat = Path.Combine(locale, "示例模组_Manual.jsonc");
            File.WriteAllText(flat, JsonSerializer.Serialize(new Dictionary<string, string> { [ownedKey] = "新人工译文" }));
            TranslationWorkbench.ReloadJson(new() { AdminKey = "secret" });
            Assert.AreEqual("新人工译文", chinese[ownedKey]);
            TranslationWorkbench.SetFileEnabledJson(new() { AdminKey = "secret", Path = "示例模组_Manual.jsonc", Enabled = false });
            Assert.AreEqual("旧人工译文", chinese[ownedKey]);
            Assert.IsTrue(File.Exists(nested));
        }
        finally { Directory.Delete(fixture, true); }
    }

    [TestMethod]
    public void OldTrackedTextDoesNotResurfaceAfterReplacingItWithAFlatFile()
    {
        var root = CreateRoot();
        const string key = Item + " Name";
        try
        {
            var locale = TranslationWorkbench.LocaleRoot(root);
            var oldFile = Path.Combine(locale, "_workbench", "20-ai.jsonc");
            var oldCache = Path.Combine(root, "cache", "workbench", "records", "ai.jsonc");
            Directory.CreateDirectory(Path.GetDirectoryName(oldFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(oldCache)!);
            var original = JsonSerializer.Serialize(new Dictionary<string, string> { [key] = "旧译文" });
            File.WriteAllText(oldFile, original);
            File.WriteAllText(oldCache, JsonSerializer.Serialize(new
            {
                entries = new Dictionary<string, object> { [key] = new { source = "Old item", translation = "旧译文" } }
            }));
            TranslationWorkbench.PrepareStorage(root);
            var chinese = new Dictionary<string, string>();
            Initialize(root, new Dictionary<string, string> { [key] = "New item" }, chinese, [Item], []);
            TranslationWorkbench.ReloadJson(new() { AdminKey = "secret" });
            Assert.IsFalse(chinese.ContainsKey(key));

            using var saved = JsonDocument.Parse(TranslationWorkbench.SaveBatchJson(new()
            {
                AdminKey = "secret", Mode = "ai", Revision = TranslationWorkbench.JobCatalog().Revision,
                Entries = [new() { Id = key, Source = "New item", Translation = "新译文" }]
            }));
            Assert.AreEqual("新译文", chinese[key]);
            File.WriteAllText(Path.Combine(locale, saved.RootElement.GetProperty("activeFile").GetString()!), "{}");
            TranslationWorkbench.ReloadJson(new() { AdminKey = "secret" });
            Assert.IsFalse(chinese.ContainsKey(key));
            Assert.AreEqual(original, File.ReadAllText(oldFile));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Initialize(string root, IReadOnlyDictionary<string,string> english,
        IReadOnlyDictionary<string,string> chinese, IEnumerable<string> items, IEnumerable<string> quests,
        bool fika = false, string adminKey = "secret") =>
        TranslationWorkbench.Initialize(root, english, chinese, items, quests, fika, adminKey);
    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "simpletranslator-workbench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}

