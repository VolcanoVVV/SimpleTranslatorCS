using System.Net;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TranslationFoundation;

namespace SimpleTranslatorCS.Tests;

[TestClass]
public class ProviderLiveSyncTests
{
    private string _root = null!;
    private const string Item = "aaaaaaaaaaaaaaaaaaaaaaaa", Key = Item + " Name";
    private Dictionary<string, string> _english = null!, _chinese = null!;
    private static JsonElement Read(string json) => JsonDocument.Parse(json).RootElement.Clone();
    [TestInitialize] public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "provider-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "translations", "ch"));
        _english = new() { [Key] = "Item" }; _chinese = new();
        TranslationWorkbench.Initialize(_root, _english, _chinese, [Item], [], adminKey: "admin");
    }
    [TestCleanup] public void Cleanup()
    {
        ServerTranslationProviders.SendOverride = null;
        Directory.Delete(_root, true);
    }
    private WorkbenchSaveRequest Save() => new() { AdminKey="admin", Mode="ai", Revision=TranslationWorkbench.JobCatalog().Revision,
        Entries=[new() { Id=Key, Source="Item", Translation="物品" }] };

    [TestMethod] public void CancelBeforeStartPreventsDelayedAndDuplicatePaidRequests()
    {
        var id = Guid.NewGuid().ToString("N");
        var owner = Guid.NewGuid().ToString("N");
        var stop = Read(ServerTranslationJobs.CancelJson(new() { JobId = id, StopPending = true, AdminKey = "admin" }, owner));
        Assert.AreEqual("cancelled", stop.GetProperty("state").GetString());
        var start = new TranslationJobRequest { RequestId = id, AdminKey = "admin" };
        Assert.AreEqual("cancelled", Read(ServerTranslationJobs.StartJson(start, _ => Assert.Fail(), sessionId: owner)).GetProperty("state").GetString());
        Assert.AreEqual("cancelled", Read(ServerTranslationJobs.StartJson(start, _ => Assert.Fail(), sessionId: owner)).GetProperty("state").GetString());
        Assert.AreEqual("cancelled", Read(ServerTranslationJobs.StatusJson(new() { JobId = id, AdminKey = "admin" }, owner)).GetProperty("state").GetString());
    }

    [TestMethod] public void CatalogSharesAllPresetsAndLimitsPlayerDestinations()
    {
        var config = new ServerProviderConfig();
        CollectionAssert.AreEqual(TranslationProviderPresets.Ai.Select(p => p.Name).ToArray(),
            ServerProviderCatalog.AiProfiles(config, true).Select(p => p.Name).ToArray());
        CollectionAssert.AreEqual(TranslationProviderPresets.Machine.Select(p => p.Endpoint).ToArray(),
            ServerProviderCatalog.MachineProfiles(config, true).Select(p => p.Settings.Endpoint).ToArray());
        Assert.AreEqual(8, ServerProviderCatalog.AiProfiles(config, false).Count);
        Assert.AreEqual(5, ServerProviderCatalog.MachineProfiles(config, false).Count);
        Assert.ThrowsException<InvalidOperationException>(() => ServerTaskPolicy.Apply(config, new() { ProfileId="builtin:ollama" }));
        Assert.ThrowsException<InvalidOperationException>(() => ServerTaskPolicy.Apply(config, new() { Mode="machine", ProfileId="builtin:libre" }));
        config.EnableBuiltinProviders=false;
        Assert.AreEqual(0, ServerProviderCatalog.AiProfiles(config, true).Count);
        Assert.ThrowsException<InvalidOperationException>(() => ServerTaskPolicy.Apply(config, new() { ProfileId="builtin:deepseek" }, true));
    }

    [TestMethod] public void SelectedProviderAndRegionApplyWithoutAllowingPlayerEndpointOverrides()
    {
        var config = new ServerProviderConfig();
        ServerTaskPolicy.Apply(config, new() { Mode="machine", ProfileId="builtin:deepl" });
        Assert.AreEqual("DeepL", config.Machine.Provider);
        Assert.AreEqual("ZH-HANS", config.Machine.TargetLanguage);
        Assert.ThrowsException<InvalidOperationException>(() => ServerTaskPolicy.Apply(config, new() { Mode="machine", ProfileId="builtin:microsoft",
            MachineOptions=new() { Endpoint="https://attacker.invalid/translate" } }));
        ServerTaskPolicy.Apply(config, new() { Mode="machine", ProfileId="builtin:microsoft", MachineOptions=new() { Region="eastasia" } });
        Assert.AreEqual("eastasia", config.Machine.Region);
        Assert.ThrowsException<InvalidOperationException>(() => ServerTaskPolicy.Apply(config, new() { ProfileId="builtin:deepseek", Options=new() { Endpoint="https://attacker.invalid/v1" } }));
        ServerTaskPolicy.Apply(config, new() { ProfileId="builtin:custom", Options=new() { Endpoint="https://my-provider.invalid/v1/chat/completions", Model="new-model" } }, true);
        Assert.AreEqual("new-model", config.Ai.Model);
        Assert.IsTrue(ServerTranslationProviders.IsReady("ai", config, new() { AiApiKey="fixture-key" }));
    }

    [TestMethod] public async Task MachineRegionAndPersonalCredentialsReachTheSelectedAdapter()
    {
        var config = new ServerProviderConfig();
        ServerTaskPolicy.Apply(config, new() { Mode="machine", ProfileId="builtin:microsoft", MachineOptions=new() { Region="eastasia" } });
        var calls=0;
        ServerTranslationProviders.SendOverride=(request, _) =>
        {
            calls++;
            Assert.AreEqual("eastasia", request.Headers.GetValues("Ocp-Apim-Subscription-Region").Single());
            Assert.AreEqual("fixture-key", request.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
            Assert.AreEqual("api.cognitive.microsofttranslator.com", request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent("[{\"translations\":[{\"text\":\"物品\"}]}]") });
        };
        var result=await ServerTranslationProviders.TranslateAsync("machine", TranslationWorkbench.JobCatalog().Entries, config,
            new() { MachineApiKey="fixture-key" }, CancellationToken.None);
        Assert.AreEqual("物品", result[Key]); Assert.AreEqual(1, calls);
    }

    [TestMethod] public void IncorrectAdministratorSecretNeverFallsBackToPaidPlayerJobs()
    {
        var calls=0;
        ServerTranslationProviders.SendOverride=(_, _) => { calls++; throw new Exception("must not call"); };
        var response=Read(ServerTranslationJobs.StartJson(new() { AdminKey="old-key", ApiKey="fixture-key" }, _ => {},
            test:true, contribution:true, sessionId:"player"));
        Assert.IsTrue(response.GetProperty("unauthorized").GetBoolean());
        Assert.AreEqual(0, calls);
    }

    [TestMethod] public async Task CustomModelDiscoveryDoesNotRequireAnAlreadySelectedModel()
    {
        var revision=ServerTranslationProviders.ConfigRevision(ServerTranslationProviders.Load().Config);
        ServerTranslationProviders.SendOverride=(request, _) =>
        {
            Assert.AreEqual(HttpMethod.Get,request.Method);
            Assert.AreEqual("https://custom.invalid/v1/models",request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent("{\"data\":[{\"id\":\"discovered-model\"}]}") });
        };
        var response=Read(await ServerTranslationProviders.ModelsJsonAsync(new() { AdminKey="admin", ApiKey="fixture-key", ProfileId="builtin:custom",
            ProviderRevision=revision, Options=new() { Model="", Endpoint="https://custom.invalid/v1/chat/completions" } }));
        Assert.IsTrue(response.GetProperty("ok").GetBoolean());
        Assert.AreEqual("discovered-model",response.GetProperty("models")[0].GetString());
    }

    [TestMethod] public void LiveRevisionChangesOnlyAfterApprovalAndUnchangedReadsAreSmall()
    {
        var initial=Read(TranslationWorkbench.LiveJson(new()));
        var cursor=initial.GetProperty("liveRevision").GetString()!;
        Assert.IsTrue(initial.GetProperty("changed").GetBoolean());
        var contribution=Save(); contribution.AdminKey=""; contribution.Mode="manual";
        Assert.IsTrue(Read(WorkbenchContributions.SubmitManualJson(contribution,Guid.NewGuid().ToString("N"),"Player")).GetProperty("ok").GetBoolean());
        var unchanged=Read(TranslationWorkbench.LiveJson(new() { LiveRevision=cursor }));
        Assert.IsFalse(unchanged.GetProperty("changed").GetBoolean());
        Assert.IsFalse(unchanged.TryGetProperty("values",out _));
        var pending=Read(WorkbenchContributions.PendingJson(new() { AdminKey="admin" })).GetProperty("entries")[0];
        Assert.IsTrue(Read(WorkbenchContributions.DecideJson(new() { AdminKey="admin", Approve=true, Translation="物品",
            SubmissionId=pending.GetProperty("submissionId").GetString()! })).GetProperty("ok").GetBoolean());
        var approved=Read(TranslationWorkbench.LiveJson(new() { LiveRevision=cursor }));
        Assert.IsTrue(approved.GetProperty("changed").GetBoolean());
        Assert.AreEqual("物品", approved.GetProperty("values").GetProperty(Key).GetString());
        Assert.AreEqual(1, approved.GetProperty("values").EnumerateObject().Count());
        Assert.AreEqual("物品", _chinese[Key]);
    }

    [TestMethod] public void HotOverlaySurvivesRebuiltDictionaryAndSynchronizesWithdrawal()
    {
        Assert.IsTrue(Read(TranslationWorkbench.SaveBatchJson(Save())).GetProperty("liveApplied").GetBoolean());
        var live=Read(TranslationWorkbench.LiveJson(new()));
        var rebuilt=new Dictionary<string,string>();
        TranslationWorkbench.CaptureBaseTranslations(rebuilt, []);
        ServerLocaleOverlay.Apply(rebuilt);
        Assert.AreEqual("物品",rebuilt[Key]);
        Assert.AreEqual(0,TranslationWorkbench.JobCatalog().Entries.Count, "Workbench tracks the rebuilt dictionary.");
        File.WriteAllText(Path.Combine(_root,"translations","ch","原版游戏_API.jsonc"),"{}");
        Assert.IsTrue(Read(TranslationWorkbench.ReloadJson(new() { AdminKey="admin" })).GetProperty("ok").GetBoolean());
        var removed=Read(TranslationWorkbench.LiveJson(new() { LiveRevision=live.GetProperty("liveRevision").GetString()! }));
        Assert.IsTrue(removed.GetProperty("changed").GetBoolean());
        CollectionAssert.Contains(removed.GetProperty("removedKeys").EnumerateArray().Select(p=>p.GetString()).ToArray(),Key);
        ServerLocaleOverlay.Apply(rebuilt); Assert.IsFalse(rebuilt.ContainsKey(Key));
    }

    [TestMethod] public void LivePayloadRestoresExistingBaselineInsteadOfRemovingIt()
    {
        _chinese[Key]="Item";
        Assert.IsTrue(Read(TranslationWorkbench.SaveBatchJson(Save())).GetProperty("ok").GetBoolean());
        File.WriteAllText(Path.Combine(_root,"translations","ch","原版游戏_API.jsonc"),"{}");
        TranslationWorkbench.ReloadJson(new() { AdminKey="admin" });
        var response=Read(TranslationWorkbench.LiveJson(new()));
        Assert.AreEqual("Item",response.GetProperty("values").GetProperty(Key).GetString());
        Assert.AreEqual(0,response.GetProperty("removedKeys").GetArrayLength());
    }
}
