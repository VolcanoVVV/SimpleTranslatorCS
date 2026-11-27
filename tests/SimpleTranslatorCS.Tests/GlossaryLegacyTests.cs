using System.Net;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TranslationFoundation;

namespace SimpleTranslatorCS.Tests;

[TestClass]
public class GlossaryLegacyTests
{
    [TestMethod] public void ReferencesMatchAliasesBoundariesAndTargetLanguageWithinBudget()
    {
        var glossary = TranslationGlossary.Parse("Ground Zero\t中心区\nGroundZero\t中心区\nraid\t战局\nhideout\t藏身处");
        foreach (var source in new[] { "Travel to Ground Zero", "GroundZero", "Visit GROUND_ZERO" })
        {
            var reference = glossary.Reference([source], "简体中文");
            StringAssert.Contains(reference, "中心区");
            Assert.IsFalse(reference.Contains("藏身处"));
            Assert.IsTrue(reference.Length <= 800);
        }
        Assert.AreEqual("", glossary.Reference(["raider"], "zh"));
        Assert.AreEqual("", glossary.Reference(["raid"], "en"));
        Assert.AreEqual("", glossary.Reference(["raid"], "zh", 10));
        Assert.AreEqual(100, glossary.Budget(1000));
        Assert.AreEqual("", TranslationGlossary.Parse("#@enabled=false\nraid\t战局").Reference(["raid"], "zh"));
        Assert.ThrowsException<InvalidDataException>(() => TranslationGlossary.Parse("raid\t战局\nRAID\t冲突"));
        Assert.ThrowsException<InvalidDataException>(() => TranslationGlossary.Parse("GroundZero 中心区"));
    }

    [TestMethod] public void GlossaryReloadPreservesEditsAndUsesImmutableSnapshots()
    {
        var root = Path.Combine(Path.GetTempPath(), "glossary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "eft.zh-CN.example.tsv"), "GroundZero\t中心区");
            var snapshot = TranslationGlossary.Load(root);
            File.WriteAllText(Path.Combine(root, "eft.zh-CN.tsv"), "raid\t战局");
            Assert.AreEqual("", TranslationGlossary.Load(root).Reference(["GroundZero"], "zh"));
            StringAssert.Contains(snapshot.Reference(["GroundZero"], "zh"), "中心区");
            File.WriteAllText(Path.Combine(root, "eft.zh-CN.tsv"), "# empty by user\n");
            Assert.AreEqual("", TranslationGlossary.Load(root).Reference(["raid"], "zh"));
            File.WriteAllBytes(Path.Combine(root, "eft.zh-CN.tsv"), new byte[TranslationGlossary.MaxFileBytes + 1]);
            Assert.ThrowsException<InvalidDataException>(() => TranslationGlossary.Load(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod] public async Task ServerAiRequestIncludesOnlyMatchingGlossaryData()
    {
        var config = new ServerProviderConfig { Glossary = TranslationGlossary.Parse("GroundZero\t中心区\nhideout\t藏身处") };
        var calls = 0;
        try
        {
            ServerTranslationProviders.SendOverride = async (request, _) =>
            {
                calls++;
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var system = payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
                StringAssert.Contains(system, "中心区");
                Assert.IsFalse(system.Contains("hideout"));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"t0001\\\":\\\"中心区\\\"}\"}}]}") };
            };
            var result = await ServerTranslationProviders.TranslateAsync("ai", [new() { Id="ground", English="GroundZero", Chinese="", Domain="item",
                    Origin="base", SourceGroup="base-game", SourceGroupName="base" }],
                config, new() { AiApiKey="fixture" }, CancellationToken.None);
            Assert.AreEqual("中心区", result["ground"]); Assert.AreEqual(1, calls);
        }
        finally { ServerTranslationProviders.SendOverride = null; }
    }

    [TestMethod] public void LegacyAndCurrentFilesReloadAndToggleIndependentlyWithoutCreatingOldDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "legacy-live-" + Guid.NewGuid().ToString("N"));
        const string id = "aaaaaaaaaaaaaaaaaaaaaaaa", key = id + " Name";
        try
        {
            Directory.CreateDirectory(root);
            TranslationWorkbench.PrepareStorage(root);
            var old = Path.Combine(root, "db", "locales", "ch");
            Assert.IsFalse(Directory.Exists(old));
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "same.jsonc"), JsonSerializer.Serialize(new Dictionary<string,string> { [key]="旧路径汉化" }));
            var current = Path.Combine(root, "translations", "ch", "Manual", "same.jsonc");
            Directory.CreateDirectory(Path.GetDirectoryName(current)!);
            File.WriteAllText(current, JsonSerializer.Serialize(new Dictionary<string,string> { [key]="当前路径汉化" }));
            var chinese = new Dictionary<string,string> { [key]="English" };
            TranslationWorkbench.Initialize(root, new Dictionary<string,string> { [key]="English" }, chinese, [id], [], adminKey:"admin");
            TranslationWorkbench.ReloadJson(new() { AdminKey="admin" });
            Assert.AreEqual("当前路径汉化", chinese[key]);
            var disabled = TranslationWorkbench.SetFileEnabledJson(new() { AdminKey="admin", Path="Manual/same.jsonc", Enabled=false });
            Assert.IsTrue(JsonDocument.Parse(disabled).RootElement.GetProperty("ok").GetBoolean());
            Assert.AreEqual("旧路径汉化", chinese[key]);
            TranslationWorkbench.SetFileEnabledJson(new() { AdminKey="admin", Path="@legacy/same.jsonc", Enabled=false });
            Assert.AreEqual("English", chinese[key]);
            Assert.IsTrue(File.Exists(Path.Combine(old, "same.jsonc")));
            Assert.AreEqual("@legacy/same.jsonc", TranslationWorkbench.NormalizeRelative(TranslationWorkbench.LocaleRoot(root), Path.Combine(old, "same.jsonc")));
            var denied = TranslationWorkbench.SetFileEnabledJson(new() { AdminKey="bad", Path="@legacy/same.jsonc", Enabled=true });
            Assert.IsFalse(JsonDocument.Parse(denied).RootElement.GetProperty("ok").GetBoolean());
            Assert.AreEqual("English", chinese[key]);
        }
        finally { Directory.Delete(root, true); }
    }
}
