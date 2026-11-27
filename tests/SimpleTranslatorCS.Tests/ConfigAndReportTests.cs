using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SimpleTranslatorCS;

namespace SimpleTranslatorCS.Tests;

[TestClass]
public class ModConfigTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "st-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(_tempDir, true);
    }

    [TestMethod]
    public void Load_MissingFile_ReturnsDefault()
    {
        var config = ModConfig.Load(_tempDir, null);
        Assert.IsFalse(config.DevMode);
        Assert.AreEqual(ModConfig.DefaultReportFile, config.ReportFile);
    }

    [TestMethod]
    public void Load_ValidJsoncWithComments_Parses()
    {
        File.WriteAllText(Path.Combine(_tempDir, "config.jsonc"), """
        {
            /* 注释 */
            "devMode": true,
            "reportFile": "my-report.jsonc", // 行注释
        }
        """);
        var config = ModConfig.Load(_tempDir, null);
        Assert.IsTrue(config.DevMode);
        Assert.AreEqual("my-report.jsonc", config.ReportFile);
    }

    [TestMethod]
    public void FikaAdminKey_FirstUseGeneratesRandomPersistentCommentedValue()
    {
        var first = WorkbenchAccess.EnsureKey(_tempDir, string.Empty);
        Assert.AreEqual(64, first.Length);
        Assert.IsTrue(first.All(Uri.IsHexDigit));
        var text = File.ReadAllText(Path.Combine(_tempDir, WorkbenchAccess.KeyFileName));
        StringAssert.Contains(text, "服务端汉化管理员密钥");
        Assert.IsFalse(File.Exists(Path.Combine(_tempDir, "config.jsonc")));
        Assert.AreEqual(first, WorkbenchAccess.EnsureKey(_tempDir, string.Empty));
        Assert.IsTrue(WorkbenchAccess.FixedEquals(first, WorkbenchAccess.EnsureKey(_tempDir, "old-key")));
        Assert.IsFalse(WorkbenchAccess.FixedEquals(first, "wrong"));
    }

    [TestMethod]
    public void AdminKey_DoesNotRewriteCommentedConfig_AndMigratesLegacyValue()
    {
        var configPath = Path.Combine(_tempDir, "config.jsonc");
        const string config = "{\n // example: \"workbenchAdminKey\": \"\"\n \"devMode\": false // trailing comment\n}\n";
        File.WriteAllText(configPath, config);
        var key = WorkbenchAccess.EnsureKey(_tempDir, string.Empty);
        Assert.AreEqual(key, WorkbenchAccess.EnsureKey(_tempDir, string.Empty));
        Assert.AreEqual(config, File.ReadAllText(configPath));
        File.Delete(Path.Combine(_tempDir, WorkbenchAccess.KeyFileName));
        Assert.AreEqual("legacy-key", WorkbenchAccess.EnsureKey(_tempDir, " legacy-key "));
        Assert.AreEqual("legacy-key", WorkbenchAccess.EnsureKey(_tempDir, "different-legacy-key"));
    }

    [TestMethod]
    public void AdminKey_CorruptDedicatedFileFailsClosedWithoutOverwriting()
    {
        var path = Path.Combine(_tempDir, WorkbenchAccess.KeyFileName);
        File.WriteAllText(path, "{ broken");
        try { WorkbenchAccess.EnsureKey(_tempDir, "legacy"); Assert.Fail("应拒绝损坏的密钥文件。"); }
        catch (JsonException) { }
        Assert.AreEqual("{ broken", File.ReadAllText(path));
    }

    [TestMethod]
    public void PullCooldown_DefaultAndBounds()
    {
        foreach (var value in new[] { "null", "\"180\"", "-1", "86401" })
        {
            File.WriteAllText(Path.Combine(_tempDir, "config.jsonc"), "{\"pullCooldownSeconds\":" + value + "}");
            Assert.AreEqual(180, ModConfig.Load(_tempDir, null).PullCooldownSeconds);
        }
        foreach (var value in new[] { 0, 30, 180, 86400 })
        {
            File.WriteAllText(Path.Combine(_tempDir, "config.jsonc"), "{\"pullCooldownSeconds\":" + value + "}");
            Assert.AreEqual(value, ModConfig.Load(_tempDir, null).PullCooldownSeconds);
        }
    }

    [TestMethod]
    public void Load_BrokenJson_ReturnsDefaultAndWarns()
    {
        File.WriteAllText(Path.Combine(_tempDir, "config.jsonc"), "{ 这不是合法 json");
        var warnings = new List<string>();
        var config = ModConfig.Load(_tempDir, warnings.Add);
        Assert.IsFalse(config.DevMode);
        Assert.IsTrue(warnings.Exists(w => w.Contains("config.jsonc")));
    }

    [TestMethod]
    public void Load_EmptyReportFile_FallsBackToDefault()
    {
        File.WriteAllText(Path.Combine(_tempDir, "config.jsonc"), """{ "devMode": true, "reportFile": "  " }""");
        var config = ModConfig.Load(_tempDir, null);
        Assert.AreEqual(ModConfig.DefaultReportFile, config.ReportFile);
    }
}

[TestClass]
public class TranslationReportTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "st-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(_tempDir, true);
    }

    [TestMethod]
    public void WriteTo_ProducesValidJsoncWithAllSections()
    {
        var report = new TranslationReport();
        report.AddLocale("a.jsonc", "key1", "中文 \"带引号\" \n换行", baseExists: true, isWinner: true, winnerFile: "a.jsonc");
        report.AddLocale("a.jsonc", "key2", "新增条目", baseExists: false, isWinner: true, winnerFile: "a.jsonc");
        report.AddDialogue("d.jsonc", "dlg1", "对话文本", matched: true);
        report.AddConflict("key1", "b.jsonc", "a.jsonc");
        report.AddUnresolvedPlaceholder("a.jsonc", "key1", "4012db58df2b5c63e64cc79f");

        var path = Path.Combine(_tempDir, "report.jsonc");
        var infos = new List<string>();
        report.WriteTo(path, infos.Add, _ => Assert.Fail("不应报错"));

        var raw = File.ReadAllText(path);
        // 报告剥离注释后必须是合法 JSON
        using var doc = JsonDocument.Parse(Jsonc.Normalize(raw));
        Assert.AreEqual("中文 \"带引号\" \n换行", doc.RootElement.GetProperty("key1").GetString());
        Assert.AreEqual("新增条目", doc.RootElement.GetProperty("key2").GetString());
        Assert.AreEqual("对话文本", doc.RootElement.GetProperty("dlg1").GetString());

        // 中文必须原样输出（不被转义为 \uXXXX），且文件带 UTF-8 BOM 供记事本识别
        StringAssert.Contains(raw, "新增条目");
        StringAssert.Contains(raw, "对话文本");
        Assert.IsTrue(File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "报告文件应带 UTF-8 BOM");

        // 各章节与统计
        StringAssert.Contains(raw, "普通本地化");
        StringAssert.Contains(raw, "Dialogue 对话");
        StringAssert.Contains(raw, "键冲突");
        StringAssert.Contains(raw, "占位符解析失败");
        Assert.IsTrue(infos.Exists(i => i.Contains("已导出")));
    }

    [TestMethod]
    public void WriteTo_WinnerAndLoserMarkedWithWinningFile()
    {
        var report = new TranslationReport();
        // key1: b.jsonc 覆盖 a.jsonc（基底存在），a.jsonc 的条目是败方
        report.AddLocale("a.jsonc", "key1", "a 的值", baseExists: true, isWinner: false, winnerFile: "b.jsonc");
        report.AddLocale("b.jsonc", "key1", "b 的值", baseExists: true, isWinner: true, winnerFile: "b.jsonc");
        // key2: 基底不存在的新增键
        report.AddLocale("a.jsonc", "key2", "新增值", baseExists: false, isWinner: true, winnerFile: "a.jsonc");

        var path = Path.Combine(_tempDir, "report.jsonc");
        report.WriteTo(path, _ => { }, _ => Assert.Fail("不应报错"));

        var lines = File.ReadAllText(path).Split('\n');

        // 败方：标注 [被覆盖] 与胜出文件
        var loserIdx = Array.FindIndex(lines, l => l.Contains("\"key1\": \"a 的值\""));
        Assert.IsTrue(loserIdx > 0, "应找到败方条目行");
        StringAssert.Contains(lines[loserIdx - 1], "[被覆盖]");
        StringAssert.Contains(lines[loserIdx - 1], "b.jsonc");

        // 胜方且基底存在：标注 [胜出] 与覆盖基底
        var winnerIdx = Array.FindIndex(lines, l => l.Contains("\"key1\": \"b 的值\""));
        Assert.IsTrue(winnerIdx > 0, "应找到胜方条目行");
        StringAssert.Contains(lines[winnerIdx - 1], "[胜出]");
        StringAssert.Contains(lines[winnerIdx - 1], "覆盖基底");

        // 胜方但基底不存在：标注新增键，且说明客户端是否引用未知
        var newKeyIdx = Array.FindIndex(lines, l => l.Contains("\"key2\": \"新增值\""));
        Assert.IsTrue(newKeyIdx > 0, "应找到新增键条目行");
        StringAssert.Contains(lines[newKeyIdx - 1], "[胜出]");
        StringAssert.Contains(lines[newKeyIdx - 1], "新增键");
        StringAssert.Contains(lines[newKeyIdx - 1], "未知");
    }

    [TestMethod]
    public void WriteTo_StatisticsMatchEntryStates()
    {
        var report = new TranslationReport();
        report.AddLocale("a.jsonc", "k1", "v1", baseExists: true, isWinner: true, winnerFile: "a.jsonc");
        report.AddLocale("a.jsonc", "k2", "v2", baseExists: false, isWinner: true, winnerFile: "a.jsonc");
        report.AddLocale("b.jsonc", "k3", "v3", baseExists: true, isWinner: false, winnerFile: "c.jsonc");
        report.AddLocale("b.jsonc", "k4", "v4", baseExists: false, isWinner: false, winnerFile: "c.jsonc");
        report.AddDialogue("d.jsonc", "dlg1", "t1", matched: true);
        report.AddDialogue("d.jsonc", "dlg2", "t2", matched: false);

        var path = Path.Combine(_tempDir, "report.jsonc");
        report.WriteTo(path, _ => { }, _ => Assert.Fail("不应报错"));

        var raw = File.ReadAllText(path);
        StringAssert.Contains(raw, "胜出 2（覆盖基底 1 / 新增键 1）");
        StringAssert.Contains(raw, "被同键覆盖 2（基底存在 1 / 基底不存在 1）");
        StringAssert.Contains(raw, "总计 4");
        StringAssert.Contains(raw, "已写入 1 / 未匹配 1");
        // 逐文件统计口径与条目状态一致
        StringAssert.Contains(raw, "a.jsonc（胜出 2/2");
    }

    [TestMethod]
    public void WriteTo_NoOverAssertionAboutInvisibleKeys()
    {
        var report = new TranslationReport();
        report.AddLocale("a.jsonc", "k1", "v1", baseExists: false, isWinner: true, winnerFile: "a.jsonc");
        report.AddDialogue("d.jsonc", "dlg1", "t", matched: false);

        var path = Path.Combine(_tempDir, "report.jsonc");
        report.WriteTo(path, _ => { }, _ => Assert.Fail("不应报错"));

        var raw = File.ReadAllText(path);
        Assert.IsFalse(raw.Contains("不会显示"), "报告不得断言基底不存在的键'不会显示'");
    }

    [TestMethod]
    public void WriteTo_EmptyReport_StillValidJson()
    {
        var report = new TranslationReport();
        var path = Path.Combine(_tempDir, "empty.jsonc");
        report.WriteTo(path, _ => { }, _ => Assert.Fail("不应报错"));

        using var doc = JsonDocument.Parse(Jsonc.Normalize(File.ReadAllText(path)));
        Assert.AreEqual(JsonValueKind.Object, doc.RootElement.ValueKind);
    }
}
