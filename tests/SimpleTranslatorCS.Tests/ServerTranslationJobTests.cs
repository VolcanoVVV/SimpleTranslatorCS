using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SimpleTranslatorCS.Tests;

[TestClass]
public class ServerTranslationJobTests
{
    [TestMethod]
    public void KeyedProvidersRequireHttpsEndpoint()
    {
        var config = new ServerProviderConfig();
        config.Ai.Endpoint = "http://example.invalid/chat/completions";
        Assert.IsFalse(ServerTranslationProviders.IsConfigured("ai", config));
        config.Ai.Provider = "Ollama 本地模型";
        Assert.IsTrue(ServerTranslationProviders.IsConfigured("ai", config));
        config.Machine.Endpoint = "http://example.invalid/translate";
        Assert.IsFalse(ServerTranslationProviders.IsConfigured("machine", config));
        config.Machine.Provider = "LibreTranslate";
        Assert.IsTrue(ServerTranslationProviders.IsConfigured("machine", config));
    }

    [TestMethod]
    public async Task FailedJob_RedactsClientKeyFromStatusAndLogs()
    {
        var root = Path.Combine(Path.GetTempPath(), "simpletranslator-redaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "db", "locales", "ch"));
        try
        {
            TranslationWorkbench.Initialize(root, new Dictionary<string, string>(),
                new Dictionary<string, string>(), [], [], adminKey: "admin-secret");
            ServerTranslationProviders.SendOverride = (_, _) =>
                throw new InvalidOperationException("Provider echoed sensitive-client-key");
            var logs = new List<string>();
            var started = JsonDocument.Parse(ServerTranslationJobs.StartJson(new TranslationJobRequest
                { AdminKey = "admin-secret", Mode = "ai", ApiKey = "sensitive-client-key" }, logs.Add, test: true));
            Assert.IsTrue(started.RootElement.GetProperty("ok").GetBoolean());
            string state = "running";
            string error = string.Empty;
            for (var attempt = 0; attempt < 100 && state == "running"; attempt++)
            {
                await Task.Delay(20);
                using var status = JsonDocument.Parse(ServerTranslationJobs.StatusJson(
                    new WorkbenchAdminRequest { AdminKey = "admin-secret" }));
                state = status.RootElement.GetProperty("state").GetString() ?? string.Empty;
                error = status.RootElement.GetProperty("error").GetString() ?? string.Empty;
            }
            Assert.AreEqual("failed", state);
            Assert.IsFalse(error.Contains("sensitive-client-key", StringComparison.Ordinal));
            Assert.IsFalse(string.Join("\n", logs).Contains("sensitive-client-key", StringComparison.Ordinal));
            var report = File.ReadAllText(Directory.GetFiles(ServerTranslationLog.Root, "*.json").Single());
            Assert.IsFalse(report.Contains("sensitive-client-key", StringComparison.Ordinal));
            StringAssert.Contains(report, "当前数据状态不符合操作要求");
            using var localReport = JsonDocument.Parse(report);
            Assert.AreEqual("失败", localReport.RootElement.GetProperty("stateLabel").GetString());
            var firstId = started.RootElement.GetProperty("jobId").GetString()!;
            using var second = JsonDocument.Parse(ServerTranslationJobs.StartJson(new TranslationJobRequest
                { AdminKey = "admin-secret", Mode = "ai", ApiKey = "sensitive-client-key" }, _ => { }, test: true));
            Assert.IsTrue(second.RootElement.GetProperty("ok").GetBoolean());
            using var previous = JsonDocument.Parse(ServerTranslationJobs.StatusJson(
                new WorkbenchAdminRequest { AdminKey = "admin-secret", JobId = firstId }));
            Assert.AreEqual("failed", previous.RootElement.GetProperty("state").GetString());
            using var hidden = JsonDocument.Parse(ServerTranslationJobs.StatusJson(
                new WorkbenchAdminRequest { History = true }, "unrelated-player"));
            Assert.AreEqual(0, hidden.RootElement.GetProperty("entries").GetArrayLength());
            for (var attempt = 0; attempt < 100; attempt++)
            {
                using var active = JsonDocument.Parse(ServerTranslationJobs.StatusJson(new() { AdminKey = "admin-secret" }));
                if (active.RootElement.GetProperty("state").GetString() != "running") break;
                await Task.Delay(20);
            }
        }
        finally
        {
            ServerTranslationProviders.SendOverride = null;
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task AiJob_UsesInitiatingClientKey_AndSavesWithoutClientBatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "simpletranslator-job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "db", "locales", "ch"));
        var key = "aaaaaaaaaaaaaaaaaaaaaaaa Name";
        var second = "bbbbbbbbbbbbbbbbbbbbbbbb Name";
        var third = "cccccccccccccccccccccccc Name";
        var english = new Dictionary<string, string> { [key] = "Test item", [second] = "Second item", [third] = "Third item" };
        var chinese = new Dictionary<string, string>();
        try
        {
            TranslationWorkbench.Initialize(root, english, chinese,
                ["aaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbb", "cccccccccccccccccccccccc"], [],
                adminKey: "admin-secret");
            var status = ServerTranslationProviders.StatusJson(new WorkbenchAdminRequest { AdminKey = "admin-secret" });
            Assert.IsFalse(status.Contains("admin-secret", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(root, "translation-provider-secrets.jsonc"),
                JsonSerializer.Serialize(new ServerProviderSecrets { AiApiKey = "provider-secret" }));
            var configPath = Path.Combine(root, "translation-providers.jsonc");
            var config = new ServerProviderConfig();
            config.Ai.BatchSize = 2;
            File.WriteAllText(configPath, JsonSerializer.Serialize(config));
            var calls = 0;
            ServerTranslationProviders.SendOverride = async (request, _) =>
            {
                calls++;
                Assert.AreEqual("Bearer client-secret", request.Headers.Authorization?.ToString());
                var body = await request.Content!.ReadAsStringAsync();
                var combined = body.Contains("Test item", StringComparison.Ordinal) &&
                    body.Contains("Second item", StringComparison.Ordinal);
                var translation = combined ? "测试物品" : body.Contains("Third item", StringComparison.Ordinal) ? "第三物品" :
                    body.Contains("Second item", StringComparison.Ordinal) ? "第二物品" : "测试物品";
                var values = new Dictionary<string, string> { ["t0001"] = translation };
                if (combined) values["unexpected"] = "ignored";
                var payload = JsonSerializer.Serialize(new { usage = new { prompt_tokens = 100, completion_tokens = 20 }, choices = new[] { new { message = new
                    { content = JsonSerializer.Serialize(values) } } } });
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            };
            var revision = TranslationWorkbench.JobCatalog().Revision;
            var missingOwnKey = ServerTranslationJobs.StartJson(new TranslationJobRequest
                { AdminKey = "admin-secret", Mode = "ai", Revision = revision }, _ => { });
            using (var missing = JsonDocument.Parse(missingOwnKey))
                Assert.IsFalse(missing.RootElement.GetProperty("ok").GetBoolean(),
                    "A legacy server secret must never pay for a new client's job.");
            var started = ServerTranslationJobs.StartJson(new TranslationJobRequest { AdminKey = "admin-secret",
                Mode = "ai", Revision = revision, ApiKey = "client-secret" }, _ => { });
            using (var json = JsonDocument.Parse(started)) Assert.IsTrue(json.RootElement.GetProperty("ok").GetBoolean());
            string state = "running";
            for (var attempt = 0; attempt < 100 && state == "running"; attempt++)
            {
                await Task.Delay(20);
                using var job = JsonDocument.Parse(ServerTranslationJobs.StatusJson(new WorkbenchAdminRequest { AdminKey = "admin-secret" }));
                state = job.RootElement.GetProperty("state").GetString() ?? string.Empty;
            }
            Assert.AreEqual("completed", state);
            Assert.AreEqual("测试物品", chinese[key]);
            Assert.IsFalse(chinese.ContainsKey(second), "Missing ID stays pending instead of resending the entire paid batch.");
            Assert.AreEqual("第三物品", chinese[third]);
            Assert.AreEqual(2, calls);
            using var jobReport = JsonDocument.Parse(ServerTranslationJobs.StatusJson(new WorkbenchAdminRequest { AdminKey = "admin-secret" }));
            Assert.AreEqual("partial", jobReport.RootElement.GetProperty("diagnostics").GetProperty("state").GetString());
            var diagnostics = jobReport.RootElement.GetProperty("diagnostics");
            Assert.AreEqual(200L, diagnostics.GetProperty("inputTokensReported").GetInt64());
            Assert.AreEqual(40L, diagnostics.GetProperty("outputTokensReported").GetInt64());
            Assert.AreEqual(2, diagnostics.GetProperty("requests").GetInt32());
            Assert.AreEqual("failed", diagnostics.GetProperty("entries").EnumerateArray().Single(entry => entry.GetProperty("id").GetString() == second).GetProperty("state").GetString());
            var review = TranslationWorkbench.ReviewJson(new WorkbenchAdminRequest { AdminKey = "admin-secret" });
            StringAssert.Contains(review, "测试物品");
            var providerStatus = ServerTranslationProviders.StatusJson(new WorkbenchAdminRequest());
            Assert.IsFalse(providerStatus.Contains("provider-secret", StringComparison.Ordinal));
            Assert.IsFalse(providerStatus.Contains("client-secret", StringComparison.Ordinal));
        }
        finally
        {
            ServerTranslationProviders.SendOverride = null;
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task AiBatchDeduplicatesSameContextAndUsesCompactUnicodePayload()
    {
        try
        {
            var calls = 0;
            ServerTranslationProviders.SendOverride = async (request, _) =>
            {
                calls++;
                using var outer = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var content = outer.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
                using var input = JsonDocument.Parse(content);
                Assert.AreEqual(1, input.RootElement.GetProperty("items").GetArrayLength());
                StringAssert.Contains(content, "测试来源");
                Assert.IsFalse(content.Contains("long-game-id", StringComparison.Ordinal));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = "{\"t0001\":\"物品名称\"}" } } } })) };
            };
            var entries = new[] { "long-game-id-one", "long-game-id-two" }.Select(id => new WorkbenchCatalogEntry
                { Id = id, Domain = "item", Origin = "mod", SourceGroup = "mod", SourceGroupName = "测试来源", English = "Item name", Chinese = "" }).ToArray();
            var result = await ServerTranslationProviders.TranslateAsync("ai", entries, new ServerProviderConfig(), new ServerProviderSecrets(), CancellationToken.None);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(2, result.Count);
            Assert.IsTrue(result.Values.All(value => value == "物品名称"));
        }
        finally { ServerTranslationProviders.SendOverride = null; }
    }

    [TestMethod]
    public async Task TimeoutDoesNotRetryOrBillAgain_AndReportsPendingRows()
    {
        var root = Path.Combine(Path.GetTempPath(), "simpletranslator-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "db", "locales", "ch"));
        try
        {
            var first = "aaaaaaaaaaaaaaaaaaaaaaaa Name";
            var second = "bbbbbbbbbbbbbbbbbbbbbbbb Name";
            TranslationWorkbench.Initialize(root, new Dictionary<string, string> { [first] = "First item", [second] = "Second item" },
                new Dictionary<string, string>(), ["aaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbb"], [], adminKey: "admin");
            var config = new ServerProviderConfig();
            config.Ai.RetryCount = 3;
            config.Ai.BatchSize = 1;
            File.WriteAllText(Path.Combine(root, "translation-providers.jsonc"), JsonSerializer.Serialize(config));
            var calls = 0;
            ServerTranslationProviders.SendOverride = (_, _) => { calls++; throw new TimeoutException("request timed out"); };
            using var start = JsonDocument.Parse(ServerTranslationJobs.StartJson(new TranslationJobRequest
                { AdminKey = "admin", Mode = "ai", ApiKey = "personal", Revision = TranslationWorkbench.JobCatalog().Revision }, _ => { }));
            Assert.IsTrue(start.RootElement.GetProperty("ok").GetBoolean());
            string state = "running";
            for (var attempt = 0; attempt < 100 && state == "running"; attempt++)
            {
                await Task.Delay(20);
                using var status = JsonDocument.Parse(ServerTranslationJobs.StatusJson(new WorkbenchAdminRequest { AdminKey = "admin" }));
                state = status.RootElement.GetProperty("state").GetString()!;
            }
            Assert.AreEqual("failed", state);
            Assert.AreEqual(1, calls, "Timeouts may already have been billed and must not be automatically resent.");
            using var report = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(ServerTranslationLog.Root, "*.json").Single()));
            var rows = report.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            Assert.AreEqual("failed", rows.Single(row => row.GetProperty("id").GetString() == first).GetProperty("state").GetString());
            Assert.AreEqual("not_started", rows.Single(row => row.GetProperty("id").GetString() == second).GetProperty("state").GetString());
        }
        finally
        {
            ServerTranslationProviders.SendOverride = null;
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task PlayerJob_QueuesCandidateUntilAdminApproves_WithoutPersistingApiKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "simpletranslator-contribution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "db", "locales", "ch"));
        var id = "dddddddddddddddddddddddd Name";
        var chinese = new Dictionary<string, string>();
        try
        {
            TranslationWorkbench.Initialize(root, new Dictionary<string, string> { [id] = "Test item" }, chinese,
                ["dddddddddddddddddddddddd"], [], adminKey: "admin-secret");
            ServerTranslationProviders.SendOverride = async (request, _) =>
            {
                Assert.AreEqual("Bearer personal-key", request.Headers.Authorization?.ToString());
                await Task.CompletedTask;
                var payload = JsonSerializer.Serialize(new { choices = new[] { new { message = new
                    { content = "{\"t0001\":\"测试物品\"}" } } } });
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            };
            var catalog = JsonDocument.Parse(WorkbenchContributions.CatalogJson("player-session"));
            Assert.AreEqual(1, catalog.RootElement.GetProperty("entries").GetArrayLength());
            var revision = catalog.RootElement.GetProperty("revision").GetString();
            var request = new TranslationJobRequest { Mode = "ai", Revision = revision!, ApiKey = "personal-key" };
            var started = JsonDocument.Parse(ServerTranslationJobs.StartJson(request, _ => { },
                contribution: true, sessionId: "player-session", playerName: "Player One"));
            Assert.IsTrue(started.RootElement.GetProperty("ok").GetBoolean());
            var other = JsonDocument.Parse(ServerTranslationJobs.StatusJson(new(), "other-session"));
            Assert.IsTrue(other.RootElement.GetProperty("unauthorized").GetBoolean());
            string state = "running";
            for (var attempt = 0; attempt < 100 && state == "running"; attempt++)
            {
                await Task.Delay(20);
                using var status = JsonDocument.Parse(ServerTranslationJobs.StatusJson(new(), "player-session"));
                state = status.RootElement.GetProperty("state").GetString() ?? string.Empty;
            }
            Assert.AreEqual("completed", state);
            Assert.IsFalse(chinese.ContainsKey(id), "Player candidates must not change the live locale.");
            Assert.AreEqual(0, JsonDocument.Parse(WorkbenchContributions.CatalogJson("another-player"))
                .RootElement.GetProperty("entries").GetArrayLength(),
                "Queued candidates must not be handed out again as untranslated work.");
            var queuePath = Path.Combine(root, "cache", "contributions", "pending-contributions.json");
            var queueText = File.ReadAllText(queuePath);
            Assert.IsFalse(queueText.Contains("personal-key", StringComparison.Ordinal));
            Assert.IsFalse(queueText.Contains("admin-secret", StringComparison.Ordinal));
            var pending = JsonDocument.Parse(WorkbenchContributions.PendingJson(
                new WorkbenchAdminRequest { AdminKey = "admin-secret" }));
            var candidate = pending.RootElement.GetProperty("entries")[0];
            Assert.AreEqual("Player One", candidate.GetProperty("player").GetString());
            Assert.IsFalse(candidate.GetProperty("stale").GetBoolean());
            var submissionId = candidate.GetProperty("submissionId").GetString();
            var denied = JsonDocument.Parse(WorkbenchContributions.DecideJson(new ContributionDecisionRequest
            { SubmissionId = submissionId!, Approve = true, Translation = "非法批准" }));
            Assert.IsTrue(denied.RootElement.GetProperty("unauthorized").GetBoolean());
            var approved = JsonDocument.Parse(WorkbenchContributions.DecideJson(new ContributionDecisionRequest
            { AdminKey = "admin-secret", SubmissionId = submissionId!, Approve = true, Translation = "测试物品" }));
            Assert.IsTrue(approved.RootElement.GetProperty("ok").GetBoolean());
            Assert.AreEqual("测试物品", chinese[id]);
            Assert.AreEqual(0, JsonDocument.Parse(WorkbenchContributions.PendingJson(
                new WorkbenchAdminRequest { AdminKey = "admin-secret" })).RootElement
                .GetProperty("entries").GetArrayLength());
        }
        finally
        {
            ServerTranslationProviders.SendOverride = null;
            Directory.Delete(root, true);
        }
    }
}
