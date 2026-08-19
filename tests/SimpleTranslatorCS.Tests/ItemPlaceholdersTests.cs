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
}
