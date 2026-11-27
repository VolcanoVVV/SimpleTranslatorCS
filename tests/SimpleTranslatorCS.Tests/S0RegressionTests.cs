using Microsoft.VisualStudio.TestTools.UnitTesting;
using SimpleTranslatorCS;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using Path = System.IO.Path;

namespace SimpleTranslatorCS.Tests;

[TestClass]
public class S0RegressionTests
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbb";

    [TestMethod]
    public void ReleaseMetadata_AcceptsItsTargetServerVersion()
    {
        var metadata = new ModMetadata();
        Assert.AreEqual("1.14.0", metadata.Version.ToString());
#if SPT40
        Assert.IsTrue(metadata.SptVersion.IsSatisfied(new SemanticVersioning.Version("4.0.9"), false));
        Assert.IsFalse(metadata.SptVersion.IsSatisfied(new SemanticVersioning.Version("4.1.2"), false));
#elif SPT50
        Assert.IsTrue(metadata.SptVersion.IsSatisfied(new SemanticVersioning.Version("5.0.0-BLEEDINGEDGEMODS"), false));
        Assert.IsFalse(metadata.SptVersion.IsSatisfied(new SemanticVersioning.Version("5.0.0"), false));
        Assert.IsFalse(metadata.SptVersion.IsSatisfied(new SemanticVersioning.Version("4.1.2"), false));
#else
        Assert.IsTrue(metadata.SptVersion.IsSatisfied(new SemanticVersioning.Version("4.1.2"), false));
        Assert.IsFalse(metadata.SptVersion.IsSatisfied(new SemanticVersioning.Version("5.0.0-BLEEDINGEDGEMODS"), false));
#endif
    }

    [TestMethod]
    public void Capabilities_AdvertisesOnlyImplementedHandshake()
    {
        using var json = System.Text.Json.JsonDocument.Parse(SimpleTranslatorRouter.BuildCapabilitiesJson());
        var root = json.RootElement;
        Assert.AreEqual("SimpleTranslatorCS", root.GetProperty("service").GetString());
        Assert.IsTrue(root.GetProperty("enabled").GetBoolean());
        Assert.AreEqual(2, root.GetProperty("protocolVersion").GetInt32());
        CollectionAssert.AreEqual(new[] { "item", "quest", "achievement", "trader", "customization", "handbook", "locale" },
            root.GetProperty("domains").EnumerateArray().Select(value => value.GetString()).ToArray());
        CollectionAssert.AreEqual(new[] { "capabilities", "catalog", "source-groups", "manual-files", "baseline", "save-batch", "hot-reload", "fika-auth", "admin-access", "pull-sync", "provider-status", "provider-test", "translation-job", "review", "client-api-key", "contribution-review", "manual-exchange", "web-player-submit", "ai-task-options", "known-profile-gateway", "job-ownership", "model-discovery", "provider-catalog", "live-sync", "live-notify", "job-request-id", "fika-workbench-policy", "review-catalog", "contribution-receipts", "job-history", "server-glossary-source" },
            root.GetProperty("features").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.AreEqual("workbench", root.GetProperty("status").GetString());
    }

    [TestMethod]
    public void AuditFormatting_SanitizesLogFieldsAndSummarizesResults()
    {
        Assert.AreEqual("line one  line two", SimpleTranslatorRouter.Clean("line one\r\nline two", 80));
        Assert.AreEqual("1234", SimpleTranslatorRouter.Clean("123456", 4));
        Assert.AreEqual("ANSI  [31m", SimpleTranslatorRouter.Clean("ANSI \u001b[31m", 80));
        Assert.AreEqual("接口返回 [已隐藏]", ServerLogText.Redact("接口返回 token-123", "  token-123  "));
        StringAssert.Contains(ServerLogText.HttpFailure(System.Net.HttpStatusCode.Unauthorized), "检查 API 密钥");
        StringAssert.Contains(ServerLogText.HttpFailure(System.Net.HttpStatusCode.RequestEntityTooLarge), "缩小批次");
        using var json = System.Text.Json.JsonDocument.Parse(
            "{\"ok\":true,\"saved\":12,\"liveApplied\":true,\"restartRequired\":false}");
        Assert.AreEqual(" · 已保存条数：12 · 已热应用：是 · 需重启：否",
            SimpleTranslatorRouter.ResponseDetails("保存批量译文", json.RootElement));
    }

    [TestMethod]
    public void DialogueApply_CaseDistinctKeysReachTheirOwnSlots()
    {
#if SPT50
        const string first = "aaaaaaaaaaaaaaaaaaaaaaaa";
        const string second = "bbbbbbbbbbbbbbbbbbbbbbbb";
        const string absent = "cccccccccccccccccccccccc";
#else
        const string first = "Key";
        const string second = "key";
        const string absent = "missing";
#endif
        var element = new TraderDialogElement
        {
            Id = default, MainVariable = default, MainTrader = default,
#if SPT50
            IsStart = false,
#endif
            SubTraders = [], Lines = [], StartPoints = [],
            LocalizationDictionary = new() { ["en"] = new() { [first] = "Upper", [second] = "Lower" } }
        };
        var dialogue = new TraderDialogs { Elements = [element] };
        var entries = new Dictionary<string, (string value, string file)>(TranslationFiles.DialogueKeyComparer)
        {
            [first] = ("大写", "a"), [second] = ("小写", "b"), [absent] = ("不新增", "c")
        };
        DialogueLocalizer.Apply(dialogue, "ch", entries, null, null, null, _ => { }, Assert.Fail);
        Assert.AreEqual("大写", element.LocalizationDictionary["ch"][first]);
        Assert.AreEqual("小写", element.LocalizationDictionary["ch"][second]);
        Assert.IsFalse(element.LocalizationDictionary["ch"].ContainsKey(absent));
        Assert.AreEqual("Upper", element.LocalizationDictionary["en"][first]);
    }

    [TestMethod]
    public void ApplyTo_CycleIsIndependentOfEntryOrder()
    {
        var entries = new Dictionary<string, string>
        {
            [A + " Name"] = "A(${" + B + "})",
            [B + " Name"] = "B(${" + A + "})",
            ["quest"] = "Find ${" + A + "}"
        };
        Dictionary<string, string> Apply(IEnumerable<KeyValuePair<string, string>> source)
        {
            var merged = TranslationMerger.Merge(new[] { ("a", (IReadOnlyDictionary<string, string>)source.ToDictionary()) });
            var target = new Dictionary<string, string>(TranslationFiles.KeyComparer);
            TranslationMerger.ApplyTo(target, merged, null);
            return target;
        }
        var first = Apply(entries);
        var second = Apply(entries.Reverse());
        foreach (var pair in first)
        {
            Assert.AreEqual(pair.Value, second[pair.Key]);
            Assert.IsFalse(pair.Value.Contains("${"));
        }
    }

    [TestMethod]
    public void ApplyTo_UsesOtherFilesAndPreservesBase()
    {
        var merged = TranslationMerger.Merge(new[] {
            ("a", (IReadOnlyDictionary<string, string>)new Dictionary<string,string> { ["quest"] = "Find ${" + A + "}" }),
            ("b", (IReadOnlyDictionary<string, string>)new Dictionary<string,string> { [A + " Name"] = "螺栓" }) });
        var target = new Dictionary<string, string>(TranslationFiles.KeyComparer) { [A + " Name"] = "Bolt", ["base"] = "保留" };
        TranslationMerger.ApplyTo(target, merged, null);
        Assert.AreEqual("Find 螺栓", target["quest"]);
        Assert.AreEqual("保留", target["base"]);
        Assert.AreEqual("Find ${" + A + "}", merged.Values["quest"]);
    }

    [TestMethod]
    public void Merge_DoesNotRetainMutableSource()
    {
        var source = new Dictionary<string, string> { ["key"] = "before" };
        var merged = TranslationMerger.Merge(new[] { ("a", (IReadOnlyDictionary<string, string>)source) });
        source["key"] = "after";
        Assert.AreEqual("before", merged.Values["key"]);
        Assert.ThrowsException<NotSupportedException>(() => ((IDictionary<string,string>)merged.Values)["key"] = "bad");
    }

    [TestMethod]
    public void Resolve_NameAndShortNameAreSeparateNodes()
    {
        var source = new Dictionary<string,string> { [A + " Name"] = "${" + A + "|short}", [A + " ShortName"] = "短名" };
        Assert.AreEqual("短名", ItemPlaceholders.Resolve("${" + A + "}", source, null));
    }

    [TestMethod]
    public void Resolve_DepthLimitFallsBackWithoutRawToken()
    {
        var source = Enumerable.Range(0, 12).ToDictionary(i => i.ToString("x24") + " Name", i => "${" + (i + 1).ToString("x24") + "}");
        var missing = new List<string>();
        var result = ItemPlaceholders.Resolve("${" + 0.ToString("x24") + "}", source, null, missing);
        Assert.AreEqual(8.ToString("x24"), result);
        CollectionAssert.AreEqual(new[] { 8.ToString("x24") }, missing);
    }

    [TestMethod]
    public void Resolve_ExponentialExpansionHasBoundedWork()
    {
        var source = Enumerable.Range(0, 8).ToDictionary(i => i.ToString("x24") + " Name",
            i => string.Concat(Enumerable.Repeat("${" + (i + 1).ToString("x24") + "}", 20)));
        source[8.ToString("x24") + " Name"] = "leaf";
        var root = 0.ToString("x24");
        var missing = new List<string>();
        Assert.AreEqual("before " + root + " after", ItemPlaceholders.Resolve("before ${" + root + "} after", source, null, missing));
        Assert.IsTrue(missing.Contains(root));
    }

    [TestMethod]
    public void Jsonc_DialoguePreservesCase_GlobalUsesTargetRule()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "{\"Key\":\"first\",\"key\":\"last\"}");
            var warnings = new List<string>();
            var dialogue = Jsonc.ParseFile(file, warnings.Add, TranslationFiles.DialogueKeyComparer)!;
            Assert.AreEqual(2, dialogue.Count);
            Assert.AreEqual(0, warnings.Count);
            var global = Jsonc.ParseFile(file, warnings.Add, TranslationFiles.KeyComparer)!;
#if SPT40
            Assert.AreEqual(2, global.Count);
            Assert.AreEqual(0, warnings.Count);
#else
            Assert.AreEqual(1, global.Count);
            Assert.AreEqual("last", global["KEY"]);
            Assert.AreEqual(1, warnings.Count);
#endif
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    public void Report_AbsentTargetCannotClaimApplied()
    {
        var file = Path.GetTempFileName();
        try
        {
            var report = new TranslationReport();
            report.AddLocale("a", "key", "value", false, true, "a", targetAvailable: false);
            report.WriteTo(file, _ => { }, Assert.Fail);
            var lines = File.ReadAllLines(file);
            var entry = Array.FindIndex(lines, line => line.Contains("\"key\": \"value\""));
            Assert.IsTrue(entry > 0);
            StringAssert.Contains(lines[entry - 1], "[未注册]");
        }
        finally { File.Delete(file); }
    }
}




