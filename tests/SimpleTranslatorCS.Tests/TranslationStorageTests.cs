using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SimpleTranslatorCS.Tests;

[TestClass]
public class TranslationStorageTests
{
    [TestMethod]
    public void LegacyImportsStayInPlaceAndSelectionMigrationIsRepeatable()
    {
        var root = Path.Combine(Path.GetTempPath(), "spt-storage-" + Guid.NewGuid().ToString("N"));
        var old = Path.Combine(root, "db", "locales", "ch");
        Directory.CreateDirectory(Path.Combine(old, "API"));
        Directory.CreateDirectory(Path.Combine(old, "dialogue"));
        Directory.CreateDirectory(Path.Combine(root, "workbench", "baselines"));
        const string text = "// comment preserved\n{\"some-id\":\"人工译文\"}";
        try
        {
            File.WriteAllText(Path.Combine(old, "manual.jsonc"), text);
            File.WriteAllText(Path.Combine(old, "dialogue", "mail.jsonc"), "{}");
            File.WriteAllText(Path.Combine(old, "API", "generated.jsonc"), "{}");
            File.WriteAllText(Path.Combine(root, "workbench", "manual-files.jsonc"), "{\"disabled\":[\"manual.jsonc\",\"dialogue/mail.jsonc\"]}");
            File.WriteAllText(Path.Combine(root, "workbench", "baselines", "manual.jsonc"), "{\"files\":{\"manual.jsonc\":{\"some-id\":\"Before\"}}}");
            TranslationWorkbench.PrepareStorage(root);
            var current = TranslationWorkbench.LocaleRoot(root);
            Assert.AreEqual(Path.Combine(root, "translations", "ch"), current);
            Assert.AreEqual(text, File.ReadAllText(Path.Combine(old, "manual.jsonc")));
            Assert.IsTrue(File.Exists(Path.Combine(old, "dialogue", "mail.jsonc")));
            Assert.IsTrue(File.Exists(Path.Combine(old, "API", "generated.jsonc")));
            Assert.IsFalse(TranslationWorkbench.IsManualFileEnabled(root, current, Path.Combine(old, "manual.jsonc")));
            using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "cache", "workbench", "baselines", "manual.jsonc")));
            Assert.AreEqual("Before", baseline.RootElement.GetProperty("files").GetProperty("@legacy/manual.jsonc").GetProperty("some-id").GetString());
            Assert.IsTrue(File.Exists(Path.Combine(root, "cache", "migration", "metadata", "baselines", "manual.jsonc")));
            TranslationWorkbench.PrepareStorage(root);
            Assert.AreEqual(text, File.ReadAllText(Path.Combine(old, "manual.jsonc")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CoexistingFilesAreNotOverwrittenAndBothRemainReadable()
    {
        var root = Path.Combine(Path.GetTempPath(), "spt-storage-conflict-" + Guid.NewGuid().ToString("N"));
        var old = Path.Combine(root, "db", "locales", "ch");
        var current = Path.Combine(root, "translations", "ch", "Manual");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(current);
        try
        {
            File.WriteAllText(Path.Combine(old, "manual.jsonc"), "old");
            File.WriteAllText(Path.Combine(current, "manual.jsonc"), "new");
            TranslationWorkbench.PrepareStorage(root);
            Assert.AreEqual("old", File.ReadAllText(Path.Combine(old, "manual.jsonc")));
            Assert.AreEqual("new", File.ReadAllText(Path.Combine(current, "manual.jsonc")));
            CollectionAssert.AreEqual(new[] { Path.Combine(old, "manual.jsonc"), Path.Combine(current, "manual.jsonc") }, TranslationWorkbench.EnumerateLocaleFiles(root).ToArray());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void BatchRestoresEarlierChangesIfALaterFileCannotBeReplaced()
    {
        var root = Path.Combine(Path.GetTempPath(), "spt-flat-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var existing = Path.Combine(root, "existing.jsonc");
            var blocked = Path.Combine(root, "blocked.jsonc");
            Directory.CreateDirectory(blocked);
            const string original = "// 保留注释\n{}";
            File.WriteAllText(existing, original);
            Assert.ThrowsException<IOException>(() => TranslationStorage.WriteBatch(new Dictionary<string, string>
            {
                [existing] = "new", [blocked] = "cannot replace directory"
            }));
            Assert.AreEqual(original, File.ReadAllText(existing));
            Assert.IsTrue(Directory.Exists(blocked));
            Assert.AreEqual(1, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void PercentLabelDoesNotBecomePrintfButPlaceholdersRemainProtected()
    {
        Assert.AreEqual("受到的伤害（%）", ServerTranslationValidation.Validate("% Damage received", "受到的伤害（%）"));
        Assert.ThrowsException<InvalidOperationException>(() => ServerTranslationValidation.Validate("Value %s", "数值 %d"));
        Assert.ThrowsException<InvalidOperationException>(() => ServerTranslationValidation.Validate("Allow 0hp", "允许无生命值"));
    }
}
