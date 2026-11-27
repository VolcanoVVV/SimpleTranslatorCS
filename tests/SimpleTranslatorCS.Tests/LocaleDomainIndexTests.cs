using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace SimpleTranslatorCS.Tests;

[TestClass]
public class LocaleDomainIndexTests
{
    private const string Item = "111111111111111111111111";
    private const string Quest = "222222222222222222222222";
    private const string QuestCondition = "333333333333333333333333";
    private const string Achievement = "444444444444444444444444";
    private const string AchievementCondition = "555555555555555555555555";
    private const string Trader = "666666666666666666666666";
    private const string Customization = "777777777777777777777777";
    private const string Handbook = "888888888888888888888888";

    [TestMethod]
    public void Classify_AdmitsOnlyExplicitStableFields()
    {
        var index = new LocaleDomainIndex(
            itemIds: [Item],
            questKeys: [Quest, Quest + " name", QuestCondition],
            achievementKeys: [Achievement + " name", Achievement + " description", Achievement + " successMessage", AchievementCondition],
            traderIds: [Trader],
            customizationIds: [Customization],
            handbookKeys: [Handbook]);

        Assert.AreEqual("item", index.Classify(Item + " Name"));
        Assert.AreEqual("item", index.Classify(Item + " ShortName"));
        Assert.AreEqual("item", index.Classify(Item + " Description"));
        Assert.IsNull(index.Classify(Item + " FullName"));

        Assert.AreEqual("quest", index.Classify(Quest));
        Assert.AreEqual("quest", index.Classify(Quest + " name"));
        Assert.AreEqual("quest", index.Classify(QuestCondition));
        Assert.IsNull(index.Classify(Quest + " technical"));

        Assert.AreEqual("achievement", index.Classify(Achievement + " name"));
        Assert.AreEqual("achievement", index.Classify(Achievement + " successMessage"));
        Assert.AreEqual("achievement", index.Classify(AchievementCondition));
        Assert.IsNull(index.Classify(Achievement + " reward"));

        Assert.AreEqual("trader", index.Classify(Trader + " FullName"));
        Assert.AreEqual("trader", index.Classify(Trader + " Location"));
        Assert.IsNull(index.Classify(Trader + " Name"));

        Assert.AreEqual("customization", index.Classify(Customization + " Name"));
        Assert.AreEqual("customization", index.Classify(Customization + " Description"));
        Assert.IsNull(index.Classify(Customization + " FullName"));

        Assert.AreEqual("handbook", index.Classify(Handbook));
        Assert.IsNull(index.Classify(Handbook + " Name"));
        Assert.IsNull(index.Classify("999999999999999999999999 Name"));
    }

    [TestMethod]
    public void ConditionLocaleKeys_TraversesConditionContainersButNotRewards()
    {
        var tree = new FakeConditionRoot
        {
            AvailableForFinish =
            [
                new FakeCondition
                {
                    Id = "outer",
                    Counter = new FakeCounter
                    {
                        Id = "counter",
                        Conditions = [new FakeCondition { Id = "nested" }]
                    },
                    VisibilityConditions = [new FakeCondition { Id = "visible" }],
                    Reward = new FakeReward { Id = "must-not-be-collected" }
                }
            ],
            Reward = new FakeReward { Id = "root-reward" }
        };

        CollectionAssert.AreEquivalent(
            new[] { "outer", "counter", "nested", "visible" },
            SimpleTranslator.ConditionLocaleKeys(tree).ToArray());
    }

    [TestMethod]
    public void Catalog_EmitsEveryStableDomainFromItsAuthoritativeIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), "simpletranslator-domains-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var english = new Dictionary<string, string>
            {
                [Item + " Name"] = "Item name",
                [Quest + " name"] = "Quest name",
                [Achievement + " name"] = "Achievement name",
                [Trader + " FullName"] = "Trader name",
                [Customization + " Name"] = "Outfit name",
                [Handbook] = "Handbook category"
            };
            TranslationWorkbench.Initialize(
                root,
                english,
                new Dictionary<string, string>(),
                [Item],
                [Quest + " name"],
                achievementKeys: [Achievement + " name"],
                traderIds: [Trader],
                customizationIds: [Customization],
                handbookKeys: [Handbook]);

            using var json = JsonDocument.Parse(TranslationWorkbench.BuildCatalogJson());
            var domains = json.RootElement.GetProperty("entries").EnumerateArray()
                .Select(entry => entry.GetProperty("domain").GetString()).ToArray();
            CollectionAssert.AreEquivalent(
                new[] { "item", "quest", "achievement", "trader", "customization", "handbook" },
                domains);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FakeConditionRoot
    {
        public List<FakeCondition> AvailableForFinish { get; init; } = [];
        public FakeReward? Reward { get; init; }
    }

    private sealed class FakeCondition
    {
        public string Id { get; init; } = string.Empty;
        public FakeCounter? Counter { get; init; }
        public List<FakeCondition> VisibilityConditions { get; init; } = [];
        public FakeReward? Reward { get; init; }
    }

    private sealed class FakeCounter
    {
        public string Id { get; init; } = string.Empty;
        public List<FakeCondition> Conditions { get; init; } = [];
    }

    private sealed class FakeReward
    {
        public string Id { get; init; } = string.Empty;
    }
}
