using Microsoft.VisualStudio.TestTools.UnitTesting;
using SimpleTranslatorCS;

namespace SimpleTranslatorCS.Tests;

[TestClass]
public class ItemPlaceholdersTests
{
    private const string Id = "4012db58df2b5c63e64cc79f";

    private static readonly Dictionary<string, string> Ch = new()
    {
        [Id + " Name"] = "全名物品",
        [Id + " ShortName"] = "短名",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["670f21e143b5fb2f15e44585 Name"] = "English Item",
    };

    [TestMethod]
    public void Resolve_NameFromPrimary()
    {
        var result = ItemPlaceholders.Resolve($"上交 15 ${{{Id}}}", Ch, En);
        Assert.AreEqual("上交 15 全名物品", result);
    }

    [TestMethod]
    public void Resolve_ShortNameWithSuffix()
    {
        var result = ItemPlaceholders.Resolve($"上交 ${{{Id}|short}}", Ch, En);
        Assert.AreEqual("上交 短名", result);
    }

    [TestMethod]
    public void Resolve_FallsBackToEnglish()
    {
        var result = ItemPlaceholders.Resolve("上交 ${670f21e143b5fb2f15e44585}", Ch, En);
        Assert.AreEqual("上交 English Item", result);
    }

    [TestMethod]
    public void Resolve_UnresolvedId_FallsBackToRawIdAndCollects()
    {
        var unresolved = new List<string>();
        var result = ItemPlaceholders.Resolve("上交 ${b5d786ccd0d59bfb69d00342}", Ch, En, unresolved);
        Assert.AreEqual("上交 b5d786ccd0d59bfb69d00342", result);
        CollectionAssert.AreEqual(new List<string> { "b5d786ccd0d59bfb69d00342" }, unresolved);
    }

    [TestMethod]
    public void Resolve_NoPlaceholder_ReturnsInputUnchanged()
    {
        const string input = "普通文本没有任何占位符";
        Assert.AreSame(input, ItemPlaceholders.Resolve(input, Ch, En));
    }

    [TestMethod]
    public void Resolve_MultiplePlaceholders_AllReplaced()
    {
        var result = ItemPlaceholders.Resolve(
            $"上交 ${{{Id}}} 和 ${{670f21e143b5fb2f15e44585}}", Ch, En);
        Assert.AreEqual("上交 全名物品 和 English Item", result);
    }

    [TestMethod]
    public void Resolve_InvalidLengthId_NotTreatedAsPlaceholder()
    {
        // 23 位（不足 24）不匹配占位符语法，原样保留
        const string input = "上交 ${4012db58df2b5c63e64cc79}";
        Assert.AreEqual(input, ItemPlaceholders.Resolve(input, Ch, En));
    }

    [TestMethod]
    public void Resolve_NullDictionaries_FallsBackToRawId()
    {
        var unresolved = new List<string>();
        var result = ItemPlaceholders.Resolve($"${{{Id}}}", null, null, unresolved);
        Assert.AreEqual(Id, result);
        Assert.AreEqual(1, unresolved.Count);
    }

    private const string IdA = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string IdB = "bbbbbbbbbbbbbbbbbbbbbbbb";
    private const string MissingId = "cccccccccccccccccccccccc";

    [TestMethod]
    public void Resolve_NestedPlaceholders_ResolveRecursively()
    {
        // 物品名本身仍含占位符：递归解析到底
        var dict = new Dictionary<string, string>
        {
            [IdA + " Name"] = $"碎片（材料：${{{IdB}}}）",
            [IdB + " Name"] = "古代螺栓",
        };

        var result = ItemPlaceholders.Resolve($"上交 ${{{IdA}}}", dict, En);

        Assert.AreEqual("上交 碎片（材料：古代螺栓）", result);
    }

    [TestMethod]
    public void Resolve_ChainThroughAnotherEntry_ReachesItemName()
    {
        // 链：文本 -> 引用翻译条目 -> 条目再引用物品名
        var dict = new Dictionary<string, string>
        {
            [IdA + " Name"] = $"关于 ${{{IdB}}} 的记录",
            [IdB + " Name"] = "古代螺栓",
        };

        var result = ItemPlaceholders.Resolve($"阅读 ${{{IdA}}}", dict, null);

        Assert.AreEqual("阅读 关于 古代螺栓 的记录", result);
    }

    [TestMethod]
    public void Resolve_NestedIdOnlyInFallback_ResolvesThroughFallback()
    {
        // primary 只有外层名字，内层 id 需要回退到 fallback 查找
        var dict = new Dictionary<string, string>
        {
            [IdA + " Name"] = $"英名 ${{{IdB}}}",
        };
        var fallback = new Dictionary<string, string>
        {
            [IdB + " Name"] = "Bolt",
        };

        var result = ItemPlaceholders.Resolve($"${{{IdA}}}", dict, fallback);

        Assert.AreEqual("英名 Bolt", result);
    }

    [TestMethod]
    public void Resolve_CircularReference_StopsAtMaxDepthWithoutThrowing()
    {
        // A 的名字引用 B、B 的名字引用 A：必须在深度上限处停止，不死循环、不栈溢出
        var dict = new Dictionary<string, string>
        {
            [IdA + " Name"] = $"${{{IdB}}}",
            [IdB + " Name"] = $"${{{IdA}}}",
        };
        var unresolved = new List<string>();

        var result = ItemPlaceholders.Resolve($"${{{IdA}}}", dict, null, unresolved);

        // 超限后保留未解析的内层占位符，缺失 id 去重后记为未解析
        StringAssert.Contains(result, "${");
        Assert.AreEqual(1, unresolved.Count);
    }

    [TestMethod]
    public void Resolve_SameMissingIdRepeatedAndNested_ReportedOnlyOnce()
    {
        // 同一键里同一个缺失 id 直接出现一次、又经递归出现一次：只报告一次
        var dict = new Dictionary<string, string>
        {
            [IdA + " Name"] = $"${{{MissingId}}}碎片",
        };
        var unresolved = new List<string>();

        var result = ItemPlaceholders.Resolve($"${{{MissingId}}} 和 ${{{IdA}}}", dict, null, unresolved);

        Assert.AreEqual(1, unresolved.Count);
        Assert.AreEqual(MissingId, unresolved[0]);
        StringAssert.Contains(result, MissingId);
    }
}
