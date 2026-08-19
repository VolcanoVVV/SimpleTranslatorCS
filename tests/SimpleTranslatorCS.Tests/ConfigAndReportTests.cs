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
        report.AddLocale("a.jsonc", "key1", "中文 \"带引号\" \n换行", true);
        report.AddLocale("a.jsonc", "key2", "未生效条目", false);
        report.AddDialogue("d.jsonc", "dlg1", "对话文本", true);
        report.AddConflict("key1", "a.jsonc", "b.jsonc");
        report.AddUnresolvedPlaceholder("a.jsonc", "key1", "4012db58df2b5c63e64cc79f");

        var path = Path.Combine(_tempDir, "report.jsonc");
        var infos = new List<string>();
        report.WriteTo(path, infos.Add, _ => Assert.Fail("不应报错"));

        var raw = File.ReadAllText(path);
        // 报告剥离注释后必须是合法 JSON
        using var doc = JsonDocument.Parse(Jsonc.Normalize(raw));
        Assert.AreEqual("中文 \"带引号\" \n换行", doc.RootElement.GetProperty("key1").GetString());
        Assert.AreEqual("未生效条目", doc.RootElement.GetProperty("key2").GetString());
        Assert.AreEqual("对话文本", doc.RootElement.GetProperty("dlg1").GetString());

        // 中文必须原样输出（不被转义为 \uXXXX），且文件带 UTF-8 BOM 供记事本识别
        StringAssert.Contains(raw, "未生效条目");
        StringAssert.Contains(raw, "对话文本");
        Assert.IsTrue(File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "报告文件应带 UTF-8 BOM");

        // 各章节与统计
        StringAssert.Contains(raw, "普通本地化");
        StringAssert.Contains(raw, "Dialogue 对话");
        StringAssert.Contains(raw, "键冲突");
        StringAssert.Contains(raw, "占位符解析失败");
        StringAssert.Contains(raw, "生效 2 / 未生效 1 / 总计 3");
        StringAssert.Contains(raw, "(生效 1/2)");
        Assert.IsTrue(infos.Exists(i => i.Contains("已导出")));
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
