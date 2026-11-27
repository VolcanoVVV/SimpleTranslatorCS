using System.Net;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TranslationFoundation;

namespace SimpleTranslatorCS.Tests;

[TestClass]
public class TranslationPolicyTests
{
    private const string Item = "aaaaaaaaaaaaaaaaaaaaaaaa", Key = Item + " Name";
    private string _root = null!;
    private Dictionary<string, string> _english = null!, _chinese = null!;
    private static JsonElement Read(string json) => JsonDocument.Parse(json).RootElement.Clone();
    [TestInitialize] public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "translation-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "translations", "ch"));
        _english = new() { [Key] = "Item" }; _chinese = new();
        TranslationWorkbench.Initialize(_root, _english, _chinese, [Item], [], adminKey: "admin");
    }
    [TestCleanup] public void Cleanup()
    {
        ServerTranslationProviders.SendOverride = null;
        TranslationWorkbench.UtcNow = () => DateTimeOffset.UtcNow;
        Directory.Delete(_root, true);
    }
    private WorkbenchSaveRequest Manual(string result = "物品", bool original = false) => new()
    { Revision = TranslationWorkbench.JobCatalog().Revision, Entries = [new() { Id = Key, Source = _english[Key], Translation = result, KeepOriginal = original }] };

    [TestMethod] public void GatewayRejectsUnknownProfileAndBoundsAuthenticatedTraffic()
    {
        var now = DateTimeOffset.UtcNow.AddDays(1); TranslationWorkbench.UtcNow = () => now;
        Assert.IsNotNull(WorkbenchGateway.Check("claimed", false, false, new()));
        var session = Guid.NewGuid().ToString();
        for (var i = 0; i < 120; i++) Assert.IsNull(WorkbenchGateway.Check(session, true, false, new()));
        Assert.IsTrue(Read(WorkbenchGateway.Check(session, true, false, new())!).GetProperty("rateLimited").GetBoolean());
        now = now.AddMinutes(1);
        Assert.IsNull(WorkbenchGateway.Check(session, true, false, new()));
        Assert.IsFalse(WorkbenchGateway.Identity(session).Contains(session));
    }

    [TestMethod] public void PlayerManualResultsNeedAdministratorReviewAndUnchangedNeedsConsent()
    {
        Assert.IsFalse(Read(WorkbenchContributions.SubmitManualJson(Manual("Item"), "player", "Player")).GetProperty("ok").GetBoolean());
        Assert.IsTrue(Read(WorkbenchContributions.SubmitManualJson(Manual("Item", true), "player", "Player")).GetProperty("ok").GetBoolean());
        Assert.AreEqual(0, _chinese.Count);
        var pending = Read(WorkbenchContributions.PendingJson(new() { AdminKey = "admin" })).GetProperty("entries")[0];
        var request = new ContributionDecisionRequest { SubmissionId = pending.GetProperty("submissionId").GetString()!, Approve = true, Translation = "Item" };
        Assert.IsTrue(Read(WorkbenchContributions.DecideJson(request)).GetProperty("unauthorized").GetBoolean());
        request.AdminKey = "admin";
        Assert.IsTrue(Read(WorkbenchContributions.DecideJson(request)).GetProperty("ok").GetBoolean());
        Assert.AreEqual(0, TranslationWorkbench.JobCatalog().Entries.Count);
        var active = Path.Combine(_root, "translations", "ch", "原版游戏_Manual.jsonc");
        File.WriteAllText(active, "{}");
        Assert.AreEqual(1, TranslationWorkbench.JobCatalog().Entries.Count, "Removing an active retain-original marker makes it eligible again.");
        _english[Key] = "New item";
        Assert.AreEqual(1, TranslationWorkbench.JobCatalog().Entries.Count);
    }

    [TestMethod] public void ManualImportAndSaveRejectStructuralDamageWithoutWriting()
    {
        _english[Key] = "Value {0}";
        var request = Manual("数值");
        Assert.IsFalse(Read(WorkbenchContributions.SubmitManualJson(request, "player", "Player")).GetProperty("ok").GetBoolean());
        request.AdminKey = "admin"; request.Mode = "manual";
        Assert.IsFalse(Read(TranslationWorkbench.SaveBatchJson(request)).GetProperty("ok").GetBoolean());
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "translations", "ch", "Manual")));
        Assert.AreEqual(0, _chinese.Count);
    }

    [TestMethod] public void StaleQueueDoesNotBlockNewSourceAndTrackedLayerSuspends()
    {
        WorkbenchContributions.SubmitManualJson(Manual(), "player", "Player");
        _english[Key] = "Updated item";
        Assert.AreEqual(1, WorkbenchContributions.FilterAvailable(TranslationWorkbench.JobCatalog().Entries).Count);
        var save = Manual("新物品"); save.AdminKey = "admin"; save.Mode = "ai";
        Assert.IsTrue(Read(TranslationWorkbench.SaveBatchJson(save)).GetProperty("ok").GetBoolean());
        _english[Key] = "Changed again";
        var path = Path.Combine(_root, "translations", "ch", "原版游戏_API.jsonc");
        Assert.AreEqual(0, TranslationWorkbench.StartupFilter(_root, _english)(path, new Dictionary<string, string> { [Key] = "新物品" }).Count);
        Assert.AreEqual(1, TranslationWorkbench.JobCatalog().Entries.Count, "Stale live value cannot disguise a missing translation.");
    }

    [TestMethod] public void TaskOptionsCannotSelectUnapprovedEndpointOrRelaxBudget()
    {
        var config = new ServerProviderConfig(); config.Ai.MaxRequests = 2; config.Ai.MaxReportedTokens = 500;
        ServerTaskPolicy.Apply(config, new() { Options = new() { MaxRequests = 0, MaxReportedTokens = 900, BatchSize = 500 } });
        Assert.AreEqual(2, config.Ai.MaxRequests); Assert.AreEqual(500, config.Ai.MaxReportedTokens); Assert.AreEqual(60, config.Ai.BatchSize);
        Assert.ThrowsException<InvalidOperationException>(() => ServerTaskPolicy.Apply(config, new() { ProfileId = "http://attacker" }));
        Assert.ThrowsException<InvalidOperationException>(() => ServerTaskPolicy.Apply(config, new() { Options = new() { Model = "unapproved" } }));
        var generic = TranslationRequestPolicy.Parameters("https://proxy.example/v1", "unknown", "auto", "off", 8192, true);
        Assert.IsFalse(generic.ContainsKey("thinking")); Assert.IsFalse(generic.ContainsKey("reasoning_effort"));
        Assert.AreEqual("low", TranslationRequestPolicy.Parameters("https://api.openai.com/v1/chat/completions", "gpt-5", "auto", "off", 8192, true)["reasoning_effort"]);
    }

    [TestMethod] public async Task SemanticMemoryReusesAcrossBatchesAndAdminSkipsPending()
    {
        var english = Enumerable.Range(1, 41).ToDictionary(i => i.ToString("x24") + " Name", _ => "Item");
        TranslationWorkbench.Initialize(_root, english, _chinese, english.Keys.Select(key => key.Split(' ')[0]), [], adminKey: "admin");
        var calls = 0;
        ServerTranslationProviders.SendOverride = (_, _) => { calls++; return Task.FromResult(Reply("物品")); };
        var start = Read(ServerTranslationJobs.StartJson(new() { AdminKey = "admin", ApiKey = "own-key", Revision = TranslationWorkbench.JobCatalog().Revision }, _ => { }));
        Assert.IsTrue(start.GetProperty("ok").GetBoolean());
        await Wait(); Assert.AreEqual(1, calls); Assert.AreEqual(41, _chinese.Count);
        TranslationWorkbench.Initialize(_root, _english, new Dictionary<string, string>(), [Item], [], adminKey: "admin");
        WorkbenchContributions.SubmitManualJson(Manual(), "another-player", "Player");
        start = Read(ServerTranslationJobs.StartJson(new() { AdminKey = "admin", ApiKey = "own-key", Revision = TranslationWorkbench.JobCatalog().Revision }, _ => { }));
        Assert.IsFalse(start.GetProperty("ok").GetBoolean()); Assert.AreEqual(1, calls);
    }

    [TestMethod] public async Task RequestBudgetStopsBeforeNextCallAndWrongJobCannotCancel()
    {
        var second = "bbbbbbbbbbbbbbbbbbbbbbbb Name"; _english[second] = "Other item";
        TranslationWorkbench.Initialize(_root, _english, _chinese, [Item, second.Split(' ')[0]], [], adminKey: "admin");
        var calls = 0;
        ServerTranslationProviders.SendOverride = (_, _) => { calls++; return Task.FromResult(Reply("物品")); };
        var request = new TranslationJobRequest { AdminKey = "admin", ApiKey = "own-key", Revision = TranslationWorkbench.JobCatalog().Revision,
            Options = new() { BatchSize = 1, MaxRequests = 1 } };
        Assert.IsTrue(Read(ServerTranslationJobs.StartJson(request, _ => { })).GetProperty("ok").GetBoolean());
        Assert.IsFalse(Read(ServerTranslationJobs.CancelJson(new() { AdminKey = "admin", JobId = "old-job" })).GetProperty("ok").GetBoolean());
        await Wait(); Assert.AreEqual(1, calls); Assert.AreEqual(1, _chinese.Count);
    }

    [TestMethod] public void OptionalLocaleCoverageOnlyAdmitsKeysFromLoadedEnglishDictionary()
    {
        _english["custom-ui-key"] = "Click here";
        TranslationWorkbench.Initialize(_root, _english, _chinese, [Item], [], adminKey: "admin", includeOtherLocaleText: true);
        var entry = TranslationWorkbench.JobCatalog().Entries.Single(item => item.Id == "custom-ui-key");
        Assert.AreEqual("locale", entry.Domain); Assert.AreEqual("other-locales", entry.SourceGroup);
        var save = Manual(); save.AdminKey = "admin"; save.Mode = "manual"; save.Entries[0].Id = "invented-key";
        Assert.IsFalse(Read(TranslationWorkbench.SaveBatchJson(save)).GetProperty("ok").GetBoolean());
    }

    [TestMethod] public void ChangedProviderConfigurationRejectsBeforeForwardingCredential()
    {
        var config = ServerTranslationProviders.Load().Config;
        var revision = ServerTranslationProviders.ConfigRevision(config);
        config.Ai.Endpoint = "https://different.example/v1/chat/completions";
        File.WriteAllText(Path.Combine(_root, "translation-providers.jsonc"), JsonSerializer.Serialize(config));
        var calls = 0; ServerTranslationProviders.SendOverride = (_, _) => { calls++; return Task.FromResult(Reply("译文")); };
        var response = Read(ServerTranslationJobs.StartJson(new() { AdminKey = "admin", ApiKey = "own-key", ProviderRevision = revision }, _ => { }, test: true));
        Assert.IsFalse(response.GetProperty("ok").GetBoolean()); Assert.AreEqual(0, calls);
    }

    [TestMethod] public async Task SplitBudgetPreservesSuccessfulHalfAndEchoNeverCompletesTranslation()
    {
        var second = "bbbbbbbbbbbbbbbbbbbbbbbb Name"; _english[second] = "Other item";
        TranslationWorkbench.Initialize(_root, _english, _chinese, [Item, second.Split(' ')[0]], [], adminKey: "admin");
        var calls = 0;
        ServerTranslationProviders.SendOverride = async (request, _) =>
        {
            calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            using var input = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            return input.RootElement.GetProperty("items").GetArrayLength() > 1 ? new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge) : Reply("物品");
        };
        Assert.IsTrue(Read(ServerTranslationJobs.StartJson(new() { AdminKey = "admin", ApiKey = "own-key", Revision = TranslationWorkbench.JobCatalog().Revision,
            Options = new() { BatchSize = 40, MaxRequests = 2, RetryCount = 0 } }, _ => { })).GetProperty("ok").GetBoolean());
        await Wait(); Assert.AreEqual(2, calls); Assert.AreEqual(1, _chinese.Count, "Successful split half survives a later budget stop.");
        var remaining = TranslationWorkbench.JobCatalog().Entries.Single();
        ServerTranslationProviders.SendOverride = (_, _) => { calls++; return Task.FromResult(Reply(remaining.English)); };
        Assert.IsTrue(Read(ServerTranslationJobs.StartJson(new() { AdminKey = "admin", ApiKey = "own-key", Revision = TranslationWorkbench.JobCatalog().Revision }, _ => { })).GetProperty("ok").GetBoolean());
        await Wait(); Assert.AreEqual(3, calls); Assert.AreEqual(1, _chinese.Count); Assert.AreEqual(1, TranslationWorkbench.JobCatalog().Entries.Count);
    }

    private static HttpResponseMessage Reply(string value) => new(HttpStatusCode.OK) { Content = new StringContent(
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = JsonSerializer.Serialize(new Dictionary<string, string> { ["t0001"] = value }) } } } })) };
    private static async Task Wait()
    {
        for (var i = 0; i < 250; i++)
        {
            if (Read(ServerTranslationJobs.StatusJson(new() { AdminKey = "admin" })).GetProperty("state").GetString() != "running") return;
            await Task.Delay(20);
        }
        Assert.Fail("Background translation job did not finish.");
    }
}

