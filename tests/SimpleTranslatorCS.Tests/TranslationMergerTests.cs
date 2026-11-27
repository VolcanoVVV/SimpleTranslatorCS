using Microsoft.VisualStudio.TestTools.UnitTesting;
using SimpleTranslatorCS;

namespace SimpleTranslatorCS.Tests;

[TestClass]
public class TranslationMergerTests
{
    private static Dictionary<string, string> Dict(params (string Key, string Value)[] entries)
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in entries)
        {
            d[k] = v;
        }
        return d;
    }

    [TestMethod]
    public void Merge_LaterFileWins()
    {
        var merged = TranslationMerger.Merge(new (string File, IReadOnlyDictionary<string, string> Entries)[]
        {
            ("a.json", Dict(("k", "旧值"), ("onlyA", "A"))),
            ("b.json", Dict(("k", "新值"))),
        });

        Assert.AreEqual("新值", merged.Values["k"]);
        Assert.AreEqual("A", merged.Values["onlyA"]);
        Assert.AreEqual("b.json", merged.WinningFile("k"));
        Assert.AreEqual("a.json", merged.WinningFile("onlyA"));
    }

    [TestMethod]
    public void Merge_ThreeFilesSameKey_RecordsFullOverrideChain()
    {
        var merged = TranslationMerger.Merge(new (string File, IReadOnlyDictionary<string, string> Entries)[]
        {
            ("a.json", Dict(("k", "1"))),
            ("b.json", Dict(("k", "2"))),
            ("c.json", Dict(("k", "3"))),
        });

        Assert.AreEqual("3", merged.Values["k"]);
        CollectionAssert.AreEqual(
            new List<string> { "a.json", "b.json", "c.json" },
            merged.OverrideChains["k"].ToList());
        Assert.AreEqual("c.json", merged.WinningFile("k"));
    }

    [TestMethod]
    public void Merge_CaseOnlyDifference_TreatedAsSameKey_AndChained()
    {
        // 两个运行时分别验证真实目标的键规则。
        var merged = TranslationMerger.Merge(new (string File, IReadOnlyDictionary<string, string> Entries)[]
        {
            ("a.json", Dict(("QuestKey", "旧"))),
            ("b.json", Dict(("questkey", "新"))),
        });

#if SPT40
        Assert.AreEqual(2, merged.Values.Count);
        Assert.AreEqual("旧", merged.Values["QuestKey"]);
        Assert.AreEqual("新", merged.Values["questkey"]);
#else
        Assert.AreEqual(1, merged.Values.Count);
        Assert.AreEqual("新", merged.Values["QUESTKEY"]);
        CollectionAssert.AreEqual(
            new List<string> { "a.json", "b.json" },
            merged.OverrideChains["questkey"].ToList());
        Assert.AreEqual("b.json", merged.WinningFile("QuestKey"));
#endif
    }

    [TestMethod]
    public void Merge_SameFileCaseVariants_ChainDoesNotRepeatSameFile()
    {
        // 源解析器（Jsonc）按 Ordinal 保留同文件内仅大小写不同的两个键；
        // 合并器视角它们是同键，但覆盖链上同一文件不应连续出现两次
        var merged = TranslationMerger.Merge(new (string File, IReadOnlyDictionary<string, string> Entries)[]
        {
            ("a.json", Dict(("Key", "1"), ("key", "2"))),
            ("b.json", Dict(("KEY", "3"))),
        });

#if SPT40
        Assert.AreEqual(3, merged.Values.Count);
        Assert.AreEqual("1", merged.Values["Key"]);
        Assert.AreEqual("2", merged.Values["key"]);
        Assert.AreEqual("3", merged.Values["KEY"]);
#else
        Assert.AreEqual(1, merged.Values.Count);
        Assert.AreEqual("3", merged.Values["key"]);
        CollectionAssert.AreEqual(
            new List<string> { "a.json", "b.json" },
            merged.OverrideChains["key"].ToList());
#endif
    }

    [TestMethod]
    public void Merge_NoOverlap_KeepsAllEntries()
    {
        var merged = TranslationMerger.Merge(new (string File, IReadOnlyDictionary<string, string> Entries)[]
        {
            ("a.json", Dict(("k1", "v1"))),
            ("b.json", Dict(("k2", "v2"))),
        });

        Assert.AreEqual(2, merged.Values.Count);
        Assert.AreEqual("a.json", merged.WinningFile("k1"));
        Assert.AreEqual("b.json", merged.WinningFile("k2"));
    }

    [TestMethod]
    public void Merge_EmptyInput_ReturnsEmpty()
    {
        var merged = TranslationMerger.Merge(Array.Empty<(string File, IReadOnlyDictionary<string, string> Entries)>());

        Assert.AreEqual(0, merged.Values.Count);
        Assert.AreEqual(0, merged.OverrideChains.Count);
        Assert.IsNull(merged.WinningFile("任何键"));
    }
}
