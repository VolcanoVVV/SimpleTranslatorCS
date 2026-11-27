using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SimpleTranslatorCS.Tests;

[TestClass]
public class WorkbenchPermissionTests
{
    private const string Item = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Key = Item + " Name";
    private string _root = null!;
    private Dictionary<string, string> _chinese = null!;
    private readonly WorkbenchAdminRequest _admin = new() { AdminKey = "secret" };
    private string Locale => Path.Combine(_root, "db", "locales", "ch");
    private static JsonElement Read(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "st-permissions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Locale);
        _chinese = new(TranslationFiles.KeyComparer) { [Key] = "Item" };
        TranslationWorkbench.Initialize(_root, new Dictionary<string, string> { [Key] = "Item" },
            _chinese, [Item], [], adminKey: "secret");
    }

    [TestCleanup]
    public void Cleanup()
    {
        TranslationWorkbench.UtcNow = () => DateTimeOffset.UtcNow;
        Directory.Delete(_root, true);
    }

    private WorkbenchSaveRequest SaveRequest() => new()
    {
        AdminKey = "secret", Mode = "ai",
        Revision = Read(TranslationWorkbench.BuildCatalogJson()).GetProperty("revision").GetString()!,
        Entries = [new() { Id = Key, Source = "Item", Translation = "译文" }]
    };

    [TestMethod]
    public void NonAdminCannotReadCatalogOrMutateEvenWithoutFika()
    {
        Assert.IsTrue(TranslationWorkbench.WriteAuthRequired);
        Assert.IsFalse(Read(TranslationWorkbench.AccessJson(new())).GetProperty("isAdmin").GetBoolean());
        Assert.IsTrue(Read(TranslationWorkbench.AccessJson(_admin)).GetProperty("isAdmin").GetBoolean());
        var save = SaveRequest(); save.AdminKey = "wrong";
        foreach (var result in new[]
        {
            TranslationWorkbench.SaveBatchJson(save),
            TranslationWorkbench.SetFileEnabledJson(new() { Path = "@legacy/manual.jsonc", Enabled = false }),
            TranslationWorkbench.UpdateBaselineJson(new()),
            TranslationWorkbench.ReloadJson(new()),
            TranslationWorkbench.BuildCatalogJson(new())
        }) Assert.IsTrue(Read(result).GetProperty("unauthorized").GetBoolean());
        Assert.AreEqual("Item", _chinese[Key]);
        Assert.IsFalse(Directory.Exists(Path.Combine(Locale, "API")));
    }

    [TestMethod]
    public void FikaGateRequiresAdminForWorkbenchAndKeepsKnownPlayersOnReadOnlySync()
    {
        var session = Guid.NewGuid().ToString("N");
        Assert.IsTrue(Read(WorkbenchGateway.Check(session, true, false, new(), requireAdmin: true)!)
            .GetProperty("unauthorized").GetBoolean());
        Assert.IsNull(WorkbenchGateway.Check(session, true, false, new()));
        Assert.IsNull(WorkbenchGateway.Check(session, true, true, _admin, requireAdmin: true));
    }

    [TestMethod]
    public void WebAdminCanQueueWhenPlayerContributionsAreDisabled()
    {
        File.WriteAllText(Path.Combine(_root, "translation-providers.jsonc"), "{\"allowPlayerContributions\": false}");
        var session = Guid.NewGuid().ToString("N");
        var submission = SaveRequest();
        submission.SubmissionId = Guid.NewGuid().ToString("N");
        var first = Read(WorkbenchContributions.SubmitManualJson(submission, session, "Admin", queueOnly: true));
        Assert.AreEqual("pending", first.GetProperty("results")[0].GetProperty("state").GetString());
        Assert.AreEqual("Item", _chinese[Key]);
        var repeat = Read(WorkbenchContributions.SubmitManualJson(submission, session, "Admin", queueOnly: true));
        var candidate = first.GetProperty("results")[0].GetProperty("candidateId").GetString()!;
        Assert.AreEqual(candidate, repeat.GetProperty("results")[0].GetProperty("candidateId").GetString());
        var query = new ContributionReceiptRequest { SubmissionId = submission.SubmissionId };
        Assert.IsFalse(Read(WorkbenchContributions.ReceiptJson(query, "other")).GetProperty("found").GetBoolean());
        var duplicate = SaveRequest();
        duplicate.SubmissionId = Guid.NewGuid().ToString("N");
        var skipped = Read(WorkbenchContributions.SubmitManualJson(duplicate, "second-admin", "Admin", queueOnly: true));
        Assert.AreEqual("skipped", skipped.GetProperty("results")[0].GetProperty("state").GetString());
        Assert.IsTrue(Read(WorkbenchContributions.DecideJson(new()
        {
            AdminKey = "secret", SubmissionId = candidate, Approve = true, Translation = "译文"
        })).GetProperty("ok").GetBoolean());
        Assert.AreEqual("译文", _chinese[Key]);
        Assert.AreEqual("approved", Read(WorkbenchContributions.ReceiptJson(query, session))
            .GetProperty("results")[0].GetProperty("state").GetString());
    }

    [TestMethod]
    public void PublishedReviewVerifiesExistingValueBeforeWritingAManualOverride()
    {
        _chinese[Key] = "原译文";
        var review = Read(TranslationWorkbench.ReviewJson(new WorkbenchReviewQuery
            { AdminKey = "secret", Page = 1, Layer = "published", Query = "Item" }));
        Assert.AreEqual(1, review.GetProperty("total").GetInt32());
        Assert.AreEqual("原译文", review.GetProperty("entries")[0].GetProperty("translation").GetString());
        var request = new WorkbenchReviewRequest
        {
            AdminKey = "secret", Mode = "published", Id = Key, Source = "Item",
            ExpectedTranslation = "另一版", Translation = "人工校订"
        };
        Assert.IsTrue(Read(TranslationWorkbench.ReviewSaveJson(request)).GetProperty("conflict").GetBoolean());
        request.ExpectedTranslation = "原译文";
        Assert.IsTrue(Read(TranslationWorkbench.ReviewSaveJson(request)).GetProperty("ok").GetBoolean());
        Assert.AreEqual("人工校订", _chinese[Key]);
    }

    [TestMethod]
    public void PullCooldownIsPerSessionAndAdminBypassesWithoutReloadingDisk()
    {
        var now = new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);
        TranslationWorkbench.UtcNow = () => now;
        File.WriteAllText(Path.Combine(Locale, "manual.jsonc"), JsonSerializer.Serialize(new Dictionary<string, string> { [Key] = "磁盘新译文" }));
        Assert.IsTrue(Read(TranslationWorkbench.PullJson(new(), "player-a")).GetProperty("ok").GetBoolean());
        Assert.AreEqual("Item", _chinese[Key], "Pull must not reload files or mutate server locale.");
        var denied = Read(TranslationWorkbench.PullJson(new(), "player-a"));
        Assert.IsTrue(denied.GetProperty("rateLimited").GetBoolean());
        Assert.AreEqual(180, denied.GetProperty("retryAfterSeconds").GetInt32());
        Assert.IsTrue(Read(TranslationWorkbench.PullJson(new(), "player-b")).GetProperty("ok").GetBoolean());
        Assert.IsTrue(Read(TranslationWorkbench.PullJson(_admin, "player-a")).GetProperty("ok").GetBoolean());
        Assert.IsTrue(Read(TranslationWorkbench.PullJson(_admin, "player-a")).GetProperty("ok").GetBoolean());
        now = now.AddSeconds(179);
        Assert.AreEqual(1, Read(TranslationWorkbench.PullJson(new(), "player-a")).GetProperty("retryAfterSeconds").GetInt32());
        now = now.AddSeconds(1);
        Assert.IsTrue(Read(TranslationWorkbench.PullJson(new(), "player-a")).GetProperty("ok").GetBoolean());
        Assert.IsFalse(Read(TranslationWorkbench.PullJson(new(), "")).GetProperty("ok").GetBoolean());
    }

    [TestMethod]
    public void CustomCooldownAndZeroAreRespected()
    {
        foreach (var seconds in new[] { 0, 7 })
        {
            TranslationWorkbench.Initialize(_root, new Dictionary<string, string>(), _chinese, [], [], adminKey: "secret", pullCooldownSeconds: seconds);
            TranslationWorkbench.PullJson(new(), "player");
            var next = Read(TranslationWorkbench.PullJson(new(), "player"));
            Assert.AreEqual(seconds == 0, next.GetProperty("ok").GetBoolean());
            if (seconds > 0) Assert.AreEqual(seconds, next.GetProperty("retryAfterSeconds").GetInt32());
        }
    }

    [TestMethod]
    public void DisabledFilesReturnRestoredValuesAndTombstonesOnEveryPull()
    {
        var file = Path.Combine(Locale, "manual.jsonc");
        File.WriteAllText(file, JsonSerializer.Serialize(new Dictionary<string, string> { [Key] = "人工", ["added"] = "新增" }));
        TranslationWorkbench.ReloadJson(_admin);
        TranslationWorkbench.SetFileEnabledJson(new() { AdminKey = "secret", Path = "@legacy/manual.jsonc", Enabled = false });
        Assert.AreEqual("Item", _chinese[Key]);
        Assert.IsFalse(_chinese.ContainsKey("added"));
        foreach (var payload in new[] { TranslationWorkbench.ReloadJson(_admin), TranslationWorkbench.PullJson(_admin, "admin"), TranslationWorkbench.PullJson(new(), "late-player") })
        {
            var json = Read(payload);
            Assert.AreEqual("Item", json.GetProperty("values").GetProperty(Key).GetString());
            Assert.IsTrue(json.GetProperty("removedKeys").EnumerateArray().Any(key => key.GetString() == "added"));
            Assert.AreEqual("ch", json.GetProperty("locale").GetString());
        }
    }

    [TestMethod]
    public void SavingNewTranslationCapturesOriginalBeforeMutation()
    {
        TranslationWorkbench.SaveBatchJson(SaveRequest());
        TranslationWorkbench.ReloadJson(_admin);
        File.WriteAllText(Path.Combine(TranslationWorkbench.LocaleRoot(_root), "原版游戏_API.jsonc"), "{}");
        TranslationWorkbench.ReloadJson(_admin);
        Assert.AreEqual("Item", _chinese[Key]);
    }

    [TestMethod]
    public void CorruptActiveOrCacheCannotBeOverwrittenBySave()
    {
        var active = Path.Combine(TranslationWorkbench.LocaleRoot(_root), "原版游戏_API.jsonc");
        var cache = Path.Combine(_root, "cache", "workbench", "records", "API", "records.jsonc");
        Directory.CreateDirectory(Path.GetDirectoryName(active)!);
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        const string damaged = "{\"old\":\"保留旧内容\",";
        File.WriteAllText(active, damaged);
        Assert.ThrowsException<InvalidDataException>(() => TranslationWorkbench.SaveBatchJson(SaveRequest()));
        Assert.AreEqual(damaged, File.ReadAllText(active));
        File.WriteAllText(active, "{}");
        File.WriteAllText(cache, damaged);
        try { TranslationWorkbench.SaveBatchJson(SaveRequest()); Assert.Fail("应拒绝损坏的缓存。"); }
        catch (JsonException) { }
        Assert.AreEqual("{}", File.ReadAllText(active));
        Assert.AreEqual(damaged, File.ReadAllText(cache));
        Assert.AreEqual("Item", _chinese[Key]);
    }

    [TestMethod]
    public void CorruptFileReloadPreservesLastKnownGoodLocale()
    {
        var file = Path.Combine(Locale, "manual.jsonc");
        File.WriteAllText(file, JsonSerializer.Serialize(new Dictionary<string, string> { [Key] = "已生效" }));
        TranslationWorkbench.ReloadJson(_admin);
        File.WriteAllText(file, "{broken");
        var error = Assert.ThrowsException<InvalidDataException>(() => TranslationWorkbench.ReloadJson(_admin));
        StringAssert.Contains(ServerLogText.Failure(error), "行号 1");
        Assert.AreEqual("已生效", _chinese[Key]);
    }
}
