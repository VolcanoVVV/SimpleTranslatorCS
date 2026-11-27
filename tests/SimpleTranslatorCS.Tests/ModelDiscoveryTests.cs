using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TranslationFoundation;

namespace SimpleTranslatorCS.Tests;

[TestClass]
public class ModelDiscoveryTests
{
    [TestMethod]
    public async Task PlayerModelDiscoveryKeepsApprovalAndRevisionGuards()
    {
        var root = Path.Combine(Path.GetTempPath(), "model-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var calls = 0;
        try
        {
            TranslationWorkbench.Initialize(root, new Dictionary<string, string>(), new Dictionary<string, string>(), [], [], adminKey: "admin");
            var (config, _) = ServerTranslationProviders.Load();
            config.Ai.Endpoint = "https://models.invalid/" + Guid.NewGuid().ToString("N") + "/v1/chat/completions";
            config.Ai.Model = "approved";
            File.WriteAllText(Path.Combine(root, "translation-providers.jsonc"), JsonSerializer.Serialize(config));
            ServerTranslationProviders.SendOverride = (request, _) =>
            {
                calls++;
                Assert.AreEqual(HttpMethod.Get, request.Method);
                Assert.AreEqual("models.invalid", request.RequestUri!.Host);
                Assert.IsTrue(request.RequestUri.AbsolutePath.EndsWith("/v1/models"));
                Assert.IsNull(request.Content);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"data\":[{\"id\":\"approved\"},{\"id\":\"new-model\"}]}", Encoding.UTF8, "application/json") });
            };
            var request = new TranslationJobRequest { ApiKey = "my-secret", ProviderRevision = ServerTranslationProviders.ConfigRevision(config) };
            using var player = JsonDocument.Parse(await ServerTranslationProviders.ModelsJsonAsync(request));
            CollectionAssert.AreEqual(new[] { "approved" }, player.RootElement.GetProperty("models").EnumerateArray().Select(id => id.GetString()).ToArray());
            request.AdminKey = "admin";
            using var admin = JsonDocument.Parse(await ServerTranslationProviders.ModelsJsonAsync(request));
            Assert.AreEqual(2, admin.RootElement.GetProperty("models").GetArrayLength());
            Assert.AreEqual(1, calls, "same credential metadata uses the cache without spending completion tokens");
            Assert.AreEqual("approved", ServerTranslationProviders.Load().Config.Ai.Model, "listing does not replace configured models");
            request.ProviderRevision = "stale";
            using var stale = JsonDocument.Parse(await ServerTranslationProviders.ModelsJsonAsync(request));
            Assert.IsFalse(stale.RootElement.GetProperty("ok").GetBoolean());
            Assert.AreEqual(1, calls, "stale configuration refuses before forwarding a key");
            Assert.ThrowsException<InvalidOperationException>(() => ServerTaskPolicy.Apply(new ServerProviderConfig(),
                new TranslationJobRequest { Options = new AiTaskOptions { Model = "new-model" } }));
            var personalConfig = new ServerProviderConfig();
            ServerTaskPolicy.Apply(personalConfig, new TranslationJobRequest { Options = new AiTaskOptions { Model = "new-model" } }, administrator: true);
            Assert.AreEqual("new-model", personalConfig.Ai.Model);
        }
        finally { ServerTranslationProviders.SendOverride = null; Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task RedirectsAndProviderExceptionBodiesAreNotExposedOrRetried()
    {
        var endpoint = "https://models.invalid/" + Guid.NewGuid().ToString("N") + "/chat/completions";
        var calls = 0;
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> redirect = (_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Content = new StringContent("secret-echo") });
        };
        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => AiModelCatalog.GetAsync(endpoint, "secret-echo", false, _ => [], CancellationToken.None, redirect));
        StringAssert.Contains(ex.Message, "302");
        Assert.IsFalse(ex.Message.Contains("secret-echo"));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => AiModelCatalog.GetAsync(endpoint, "secret-echo", false, _ => [], CancellationToken.None, redirect));
        Assert.AreEqual(1, calls);
        var failed = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => AiModelCatalog.GetAsync(endpoint, "different-key", false, _ => [], CancellationToken.None,
            (_, _) => throw new InvalidOperationException("different-key")));
        Assert.IsFalse(failed.Message.Contains("different-key"));
    }

    [TestMethod]
    public void ModelUrlsAndLogRootsRespectConnectionBoundaries()
    {
        Assert.AreEqual("https://generativelanguage.googleapis.com/v1beta/openai/models",
            AiModelCatalog.ModelsUri("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions?key=never-copy").AbsoluteUri);
        Assert.ThrowsException<InvalidOperationException>(() => AiModelCatalog.ModelsUri("http://remote.invalid/v1/chat/completions"));
        Assert.ThrowsException<InvalidOperationException>(() => AiModelCatalog.ModelsUri("https://user:pass@example.invalid/v1/chat/completions"));
        CollectionAssert.AreEqual(new[] { "gemini-x" }, AiModelCatalog.Normalize(new[] { "models/gemini-x", "models/gemini-x", "bad\nmodel" }, true));
        var root = Path.Combine(Path.GetTempPath(), "logs-layout-" + Guid.NewGuid().ToString("N"));
        Assert.AreEqual(Path.Combine(root, "API翻译日志", "SimpleTranslatorCS"),
            TranslationLogPath.ForProject(Path.Combine(root, "user", "mods", "SimpleTranslatorCS"), "SimpleTranslatorCS"));
        Assert.AreEqual(Path.Combine(root, "API翻译日志", "ConfigurationManager.zh-cn"),
            TranslationLogPath.ForProject(Path.Combine(root, "BepInEx", "plugins"), "ConfigurationManager.zh-cn"));
    }
}

