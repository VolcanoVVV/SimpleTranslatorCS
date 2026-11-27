using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;
#if SPT40
using RouterLogger = SPTarkov.Server.Core.Models.Utils.ISptLogger<SimpleTranslatorCS.SimpleTranslatorRouter>;
#else
using RouterLogger = SPTarkov.Common.Models.Logging.ISptLogger<SimpleTranslatorCS.SimpleTranslatorRouter>;
#endif

namespace SimpleTranslatorCS;

/// <summary>
/// Workbench endpoints. Administrator mutations and player pulls are separate operations;
/// pull throttling uses the server-provided player session.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class SimpleTranslatorRouter : StaticRouter
{
    public const string CapabilitiesRoute = "/simpletranslator/v1/capabilities";
    internal const string DiscoveryKey = "__SimpleTranslatorCS_WorkbenchProtocol";
    public const string LiveRoute = "/simpletranslator/v1/workbench/live";
    public const string ModelsRoute = "/simpletranslator/v1/workbench/models";
    public const string CatalogRoute = "/simpletranslator/v1/workbench/catalog";
    public const string FileSelectionRoute = "/simpletranslator/v1/workbench/files";
    public const string BaselineRoute = "/simpletranslator/v1/workbench/baseline";
    public const string SaveRoute = "/simpletranslator/v1/workbench/save";
    public const string ReloadRoute = "/simpletranslator/v1/workbench/reload";
    public const string PullRoute = "/simpletranslator/v1/workbench/pull";
    public const string AccessRoute = "/simpletranslator/v1/workbench/access";
    public const string ProviderStatusRoute = "/simpletranslator/v1/workbench/provider-status";
    public const string ProviderTestRoute = "/simpletranslator/v1/workbench/provider-test";
    public const string JobStartRoute = "/simpletranslator/v1/workbench/job-start";
    public const string JobStatusRoute = "/simpletranslator/v1/workbench/job-status";
    public const string JobCancelRoute = "/simpletranslator/v1/workbench/job-cancel";
    public const string WebPlayerSubmitRoute = "/simpletranslator/v1/workbench/web-player-submit";
    public const string ManualSubmitRoute = "/simpletranslator/v1/workbench/manual-submit";
    public const string ReviewRoute = "/simpletranslator/v1/workbench/review";
    public const string ReviewSaveRoute = "/simpletranslator/v1/workbench/review-save";
    public const string ContributionCatalogRoute = "/simpletranslator/v1/workbench/contribution-catalog";
    public const string ContributionStartRoute = "/simpletranslator/v1/workbench/contribution-start";
    public const string ContributionReceiptRoute = "/simpletranslator/v1/workbench/contribution-receipt";
    public const string ContributionPendingRoute = "/simpletranslator/v1/workbench/contribution-pending";
    public const string ContributionDecisionRoute = "/simpletranslator/v1/workbench/contribution-decision";

    public SimpleTranslatorRouter(JsonUtil jsonUtil, RouterLogger logger, SaveServer saveServer)
        : base(jsonUtil, new RouteAction[]
        {
            new RouteAction<EmptyRequestData>(CapabilitiesRoute,
#if SPT40
                (url, request, sessionId, output) => new ValueTask<string>(BuildCapabilitiesJson())),
            new RouteAction<WorkbenchAdminRequest>(CatalogRoute,
                (url, request, sessionId, output) => new ValueTask<string>(Audit(logger, saveServer, "读取翻译目录", request,
                    sessionId.ToString(), () => TranslationWorkbench.BuildCatalogJson(request)))),
            new RouteAction<WorkbenchAdminRequest>(AccessRoute,
                (url, request, sessionId, output) => new ValueTask<string>(Audit(logger, saveServer, "校验管理员密钥", request,
                    sessionId.ToString(), () => TranslationWorkbench.AccessJson(request), allowWithoutFikaKey: true))),
            new RouteAction<WorkbenchAdminRequest>(PullRoute,
                (url, request, sessionId, output) => new ValueTask<string>(Audit(logger, saveServer, "主动拉取译文", request,
                    sessionId.ToString(), () => TranslationWorkbench.PullJson(request, sessionId.ToString())))),
            new RouteAction<WorkbenchFileSelectionRequest>(FileSelectionRoute,
                (url, request, sessionId, output) => new ValueTask<string>(Audit(logger, saveServer, "切换人工译文文件", request,
                    sessionId.ToString(), () => TranslationWorkbench.SetFileEnabledJson(request),
                    $"文件：{Clean(request.Path, 160)}，启用：{ServerLogText.YesNo(request.Enabled)}"))),
            new RouteAction<WorkbenchAdminRequest>(BaselineRoute,
                (url, request, sessionId, output) => new ValueTask<string>(Audit(logger, saveServer, "更新原文基准", request,
                    sessionId.ToString(), () => TranslationWorkbench.UpdateBaselineJson(request)))),
            new RouteAction<WorkbenchSaveRequest>(SaveRoute,
                (url, request, sessionId, output) => new ValueTask<string>(Audit(logger, saveServer, "保存批量译文", request,
                    sessionId.ToString(), () => TranslationWorkbench.SaveBatchJson(request),
                    $"类型：{ServerLogText.Mode(request.Mode)}，提交 {request.Entries?.Count ?? 0} 条"))),
            new RouteAction<WorkbenchAdminRequest>(ReloadRoute,
                (url, request, sessionId, output) => new ValueTask<string>(Audit(logger, saveServer, "更新并重载译文", request,
                    sessionId.ToString(), () => TranslationWorkbench.ReloadJson(request)))),
            new RouteAction<WorkbenchAdminRequest>(ProviderStatusRoute,
                (url, request, sessionId, output) => new ValueTask<string>(Audit(logger, saveServer, "校对供应商配置", request,
                    sessionId.ToString(), () => ServerTranslationProviders.StatusJson(request)))),
            new RouteAction<WorkbenchLiveRequest>(LiveRoute,
                (url, request, sessionId, output) => new ValueTask<string>(LiveResponse(saveServer, request, sessionId.ToString()))),
            new RouteAction<TranslationJobRequest>(ModelsRoute,
                (url, request, sessionId, output) => ModelsResponseAsync(logger, saveServer, request, sessionId.ToString())),
            new RouteAction<WorkbenchSaveRequest>(ManualSubmitRoute,
                (url, request, sessionId, output) => new ValueTask<string>(ManualSubmission(logger, saveServer, request, sessionId.ToString(), playerOnly: false))),
            new RouteAction<WorkbenchSaveRequest>(WebPlayerSubmitRoute,
                (url, request, sessionId, output) => new ValueTask<string>(ManualSubmission(logger, saveServer, request, sessionId.ToString(), playerOnly: true))),
            new RouteAction<WorkbenchAdminRequest>(ContributionCatalogRoute,
                (url, request, sessionId, output) => new ValueTask<string>(Audit(logger, saveServer, "读取贡献目录", request, sessionId.ToString(), () => WorkbenchContributions.CatalogJson(sessionId.ToString())))),
            new RouteAction<TranslationJobRequest>(ProviderTestRoute,
                (url, request, sessionId, output) => new ValueTask<string>(Audit(logger, saveServer, "测试翻译接口", request,
                    sessionId.ToString(), () => ServerTranslationJobs.StartJson(request, message => logger.Info(message),
                        test: true, contribution: !TranslationWorkbench.IsAuthorized(request.AdminKey),
                        sessionId: sessionId.ToString()), $"类型：{ServerLogText.Mode(request.Mode)}"))),
            new RouteAction<TranslationJobRequest>(JobStartRoute,
                (url, request, sessionId, output) => new ValueTask<string>(Audit(logger, saveServer, "发起服务端翻译", request,
                    sessionId.ToString(), () => ServerTranslationJobs.StartJson(request, message => logger.Info(message)), $"类型：{ServerLogText.Mode(request.Mode)}"))),
            new RouteAction<TranslationJobRequest>(ContributionStartRoute,
                (url, request, sessionId, output) => new ValueTask<string>(Audit(logger, saveServer, "提交候选译文", request,
                    sessionId.ToString(), () => ServerTranslationJobs.StartJson(request, message => logger.Info(message),
                        contribution: true, sessionId: sessionId.ToString(),
                        playerName: ResolvePlayer(saveServer, sessionId.ToString())), $"类型：{ServerLogText.Mode(request.Mode)}"))),
            new RouteAction<WorkbenchAdminRequest>(JobStatusRoute,
                (url, request, sessionId, output) => new ValueTask<string>(Audit(logger, saveServer, "读取任务状态", request, sessionId.ToString(), () => ServerTranslationJobs.StatusJson(request, sessionId.ToString())))),
            new RouteAction<WorkbenchAdminRequest>(JobCancelRoute,
                (url, request, sessionId, output) => new ValueTask<string>(Audit(logger, saveServer, "取消服务端翻译", request,
                    sessionId.ToString(), () => ServerTranslationJobs.CancelJson(request, sessionId.ToString())))),
            new RouteAction<WorkbenchReviewQuery>(ReviewRoute,
                (url, request, sessionId, output) => new ValueTask<string>(Audit(logger, saveServer, "读取译文校对", request, sessionId.ToString(), () => TranslationWorkbench.ReviewJson(request)))),
            new RouteAction<ContributionReceiptRequest>(ContributionReceiptRoute,
                (url, request, sessionId, output) => new ValueTask<string>(Audit(logger, saveServer, "查询投稿回执", request, sessionId.ToString(),
                    () => WorkbenchContributions.ReceiptJson(request, sessionId.ToString())))),
            new RouteAction<WorkbenchAdminRequest>(ContributionPendingRoute,
                (url, request, sessionId, output) => new ValueTask<string>(Audit(logger, saveServer, "读取候选译文", request, sessionId.ToString(), () => WorkbenchContributions.PendingJson(request)))),
            new RouteAction<ContributionDecisionRequest>(ContributionDecisionRoute,
                (url, request, sessionId, output) => new ValueTask<string>(Audit(logger, saveServer, "审核候选译文", request,
                    sessionId.ToString(), () => WorkbenchContributions.DecideJson(request),
                    $"投稿编号：{Clean(request.SubmissionId, 40)}，批准：{ServerLogText.YesNo(request.Approve)}"))),
            new RouteAction<WorkbenchReviewRequest>(ReviewSaveRoute,
                (url, request, sessionId, output) => new ValueTask<string>(Audit(logger, saveServer, "校对并保存译文", request,
                    sessionId.ToString(), () => TranslationWorkbench.ReviewSaveJson(request), $"类型：{ServerLogText.Mode(request.Mode)}，词条：{Clean(request.Id, 80)}")))
#else
                (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(BuildCapabilitiesJson())),
            new RouteAction<WorkbenchAdminRequest>(CatalogRoute,
                (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(Audit(logger, saveServer, "读取翻译目录", request,
                    sessionId.ToString(), () => TranslationWorkbench.BuildCatalogJson(request)))),
            new RouteAction<WorkbenchAdminRequest>(AccessRoute,
                (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(Audit(logger, saveServer, "校验管理员密钥", request,
                    sessionId.ToString(), () => TranslationWorkbench.AccessJson(request), allowWithoutFikaKey: true))),
            new RouteAction<WorkbenchAdminRequest>(PullRoute,
                (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(Audit(logger, saveServer, "主动拉取译文", request,
                    sessionId.ToString(), () => TranslationWorkbench.PullJson(request, sessionId.ToString())))),
            new RouteAction<WorkbenchFileSelectionRequest>(FileSelectionRoute,
                (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(Audit(logger, saveServer, "切换人工译文文件", request,
                    sessionId.ToString(), () => TranslationWorkbench.SetFileEnabledJson(request),
                    $"文件：{Clean(request.Path, 160)}，启用：{ServerLogText.YesNo(request.Enabled)}"))),
            new RouteAction<WorkbenchAdminRequest>(BaselineRoute,
                (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(Audit(logger, saveServer, "更新原文基准", request,
                    sessionId.ToString(), () => TranslationWorkbench.UpdateBaselineJson(request)))),
            new RouteAction<WorkbenchSaveRequest>(SaveRoute,
                (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(Audit(logger, saveServer, "保存批量译文", request,
                    sessionId.ToString(), () => TranslationWorkbench.SaveBatchJson(request),
                    $"类型：{ServerLogText.Mode(request.Mode)}，提交 {request.Entries?.Count ?? 0} 条"))),
            new RouteAction<WorkbenchAdminRequest>(ReloadRoute,
                (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(Audit(logger, saveServer, "更新并重载译文", request,
                    sessionId.ToString(), () => TranslationWorkbench.ReloadJson(request)))),
            new RouteAction<WorkbenchAdminRequest>(ProviderStatusRoute,
                (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(Audit(logger, saveServer, "校对供应商配置", request,
                    sessionId.ToString(), () => ServerTranslationProviders.StatusJson(request)))),
            new RouteAction<WorkbenchLiveRequest>(LiveRoute,
                (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(LiveResponse(saveServer, request, sessionId.ToString()))),
            new RouteAction<TranslationJobRequest>(ModelsRoute,
                (url, request, sessionId, output, cancellationToken) => ModelsResponseAsync(logger, saveServer, request, sessionId.ToString())),
            new RouteAction<WorkbenchSaveRequest>(ManualSubmitRoute,
                (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(ManualSubmission(logger, saveServer, request, sessionId.ToString(), playerOnly: false))),
            new RouteAction<WorkbenchSaveRequest>(WebPlayerSubmitRoute,
                (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(ManualSubmission(logger, saveServer, request, sessionId.ToString(), playerOnly: true))),
            new RouteAction<WorkbenchAdminRequest>(ContributionCatalogRoute,
                (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(Audit(logger, saveServer, "读取贡献目录", request, sessionId.ToString(), () => WorkbenchContributions.CatalogJson(sessionId.ToString())))),
            new RouteAction<TranslationJobRequest>(ProviderTestRoute,
                (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(Audit(logger, saveServer, "测试翻译接口", request,
                    sessionId.ToString(), () => ServerTranslationJobs.StartJson(request, message => logger.Info(message),
                        test: true, contribution: !TranslationWorkbench.IsAuthorized(request.AdminKey),
                        sessionId: sessionId.ToString()), $"类型：{ServerLogText.Mode(request.Mode)}"))),
            new RouteAction<TranslationJobRequest>(JobStartRoute,
                (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(Audit(logger, saveServer, "发起服务端翻译", request,
                    sessionId.ToString(), () => ServerTranslationJobs.StartJson(request, message => logger.Info(message)), $"类型：{ServerLogText.Mode(request.Mode)}"))),
            new RouteAction<TranslationJobRequest>(ContributionStartRoute,
                (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(Audit(logger, saveServer, "提交候选译文", request,
                    sessionId.ToString(), () => ServerTranslationJobs.StartJson(request, message => logger.Info(message),
                        contribution: true, sessionId: sessionId.ToString(),
                        playerName: ResolvePlayer(saveServer, sessionId.ToString())), $"类型：{ServerLogText.Mode(request.Mode)}"))),
            new RouteAction<WorkbenchAdminRequest>(JobStatusRoute,
                (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(Audit(logger, saveServer, "读取任务状态", request, sessionId.ToString(), () => ServerTranslationJobs.StatusJson(request, sessionId.ToString())))),
            new RouteAction<WorkbenchAdminRequest>(JobCancelRoute,
                (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(Audit(logger, saveServer, "取消服务端翻译", request,
                    sessionId.ToString(), () => ServerTranslationJobs.CancelJson(request, sessionId.ToString())))),
            new RouteAction<WorkbenchReviewQuery>(ReviewRoute,
                (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(Audit(logger, saveServer, "读取译文校对", request, sessionId.ToString(), () => TranslationWorkbench.ReviewJson(request)))),
            new RouteAction<ContributionReceiptRequest>(ContributionReceiptRoute,
                (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(Audit(logger, saveServer, "查询投稿回执", request, sessionId.ToString(),
                    () => WorkbenchContributions.ReceiptJson(request, sessionId.ToString())))),
            new RouteAction<WorkbenchAdminRequest>(ContributionPendingRoute,
                (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(Audit(logger, saveServer, "读取候选译文", request, sessionId.ToString(), () => WorkbenchContributions.PendingJson(request)))),
            new RouteAction<ContributionDecisionRequest>(ContributionDecisionRoute,
                (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(Audit(logger, saveServer, "审核候选译文", request,
                    sessionId.ToString(), () => WorkbenchContributions.DecideJson(request),
                    $"投稿编号：{Clean(request.SubmissionId, 40)}，批准：{ServerLogText.YesNo(request.Approve)}"))),
            new RouteAction<WorkbenchReviewRequest>(ReviewSaveRoute,
                (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(Audit(logger, saveServer, "校对并保存译文", request,
                    sessionId.ToString(), () => TranslationWorkbench.ReviewSaveJson(request), $"类型：{ServerLogText.Mode(request.Mode)}，词条：{Clean(request.Id, 80)}")))
#endif
        })
    {
    }

    internal static string BuildCapabilitiesJson()
    {
        var response = new Dictionary<string, object>
        {
            ["service"] = "SimpleTranslatorCS",
            ["enabled"] = true,
            ["serverVersion"] = ModInfo.Version,
            ["protocolVersion"] = 2,
            ["domains"] = new[] { "item", "quest", "achievement", "trader", "customization", "handbook", "locale" },
            ["features"] = new[] { "capabilities", "catalog", "source-groups", "manual-files", "baseline", "save-batch", "hot-reload", "fika-auth", "admin-access", "pull-sync", "provider-status", "provider-test", "translation-job", "review", "client-api-key", "contribution-review", "manual-exchange", "web-player-submit", "ai-task-options", "known-profile-gateway", "job-ownership", "model-discovery", "provider-catalog", "live-sync", "live-notify", "job-request-id", "fika-workbench-policy", "review-catalog", "contribution-receipts", "job-history", "server-glossary-source" },
            ["pullCooldownSeconds"] = TranslationWorkbench.PullCooldownSeconds,
            ["fikaDetected"] = TranslationWorkbench.FikaDetected,
            ["writeAuthRequired"] = TranslationWorkbench.WriteAuthRequired,
            ["status"] = "workbench"
        };
        return JsonSerializer.Serialize(response);
    }

    private static string ManualSubmission(RouterLogger logger, SaveServer saveServer, WorkbenchSaveRequest request,
        string sessionId, bool playerOnly)
    {
        return Audit(logger, saveServer, "提交人工译文", request, sessionId,
            () => WorkbenchContributions.SubmitManualJson(request!, sessionId, ResolvePlayer(saveServer, sessionId), queueOnly: playerOnly));
    }

    private static string LiveResponse(SaveServer saveServer, WorkbenchLiveRequest request, string sessionId)
    {
        var denied = WorkbenchGateway.Check(sessionId, HasProfile(saveServer, sessionId), false, request);
        return denied ?? TranslationWorkbench.LiveJson(request);
    }

    private static async ValueTask<string> ModelsResponseAsync(RouterLogger logger, SaveServer saveServer, TranslationJobRequest request, string sessionId)
    {
        var admin = (request?.AdminKey?.Length ?? 0) <= 256 && TranslationWorkbench.IsAuthorized(request?.AdminKey);
        var denied = WorkbenchGateway.Check(sessionId, HasProfile(saveServer, sessionId), admin, request,
            requireAdmin: TranslationWorkbench.FikaDetected);
        if (denied != null) return denied;
        var response = await ServerTranslationProviders.ModelsJsonAsync(request!).ConfigureAwait(false);
        ServerTranslationLog.Write("模型查询", WorkbenchGateway.Identity(sessionId), response.Contains("\"ok\":true") ? "成功" : "失败");
        return response;
    }

    private static string Audit(RouterLogger logger, SaveServer saveServer, string operation, WorkbenchAdminRequest? request,
        string sessionId, Func<string> action, string requestDetails = "", bool allowWithoutFikaKey = false)
    {
        var session = string.IsNullOrWhiteSpace(sessionId) ? "无有效会话" : WorkbenchGateway.Identity(sessionId);
        var player = ResolvePlayer(saveServer, sessionId);
        var admin = (request?.AdminKey?.Length ?? 0) <= 256 && TranslationWorkbench.IsAuthorized(request?.AdminKey);
        var role = admin ? "管理员" : "普通玩家";
        var denied = WorkbenchGateway.Check(sessionId, HasProfile(saveServer, sessionId), admin, request,
            requireAdmin: TranslationWorkbench.FikaDetected && !allowWithoutFikaKey);
        if (denied != null)
        {
            using var rejection = JsonDocument.Parse(denied);
            var reason = rejection.RootElement.GetProperty("error").GetString() ?? "请求未通过检查。";
            ServerTranslationLog.Write(operation, session, "拒绝", reason);
            return denied;
        }
        try
        {
            var response = action();
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            var ok = !root.TryGetProperty("ok", out var okProperty) || okProperty.ValueKind == JsonValueKind.True;
            var unauthorized = root.TryGetProperty("unauthorized", out var unauthorizedProperty)
                               && unauthorizedProperty.ValueKind == JsonValueKind.True;
            var rateLimited = root.TryGetProperty("rateLimited", out var rateProperty)
                              && rateProperty.ValueKind == JsonValueKind.True;
            var details = ResponseDetails(operation, root);
            var adminCheckFailed = operation == "校验管理员密钥" &&
                                   root.TryGetProperty("isAdmin", out var isAdmin) && isAdmin.ValueKind == JsonValueKind.False;
            var result = adminCheckFailed ? "密钥未通过" : ok ? "成功" : rateLimited ? "冷却或限流" : unauthorized ? "权限拒绝" : "失败";
            var message = $"[文本汉化][审计] {operation} · {result} · 玩家：{player} · 会话摘要：{session} · 身份：{role}";
            if (!string.IsNullOrWhiteSpace(requestDetails)) message += " · " + requestDetails;
            var translation = request as TranslationJobRequest;
            string Redact(string value) => ServerLogText.Redact(value, request?.AdminKey, translation?.ApiKey, translation?.AppId);
            if (!ok && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                details += " · 原因：" + Clean(Redact(error.GetString() ?? ""), 240);
            message = Redact(message + details);
            details = Redact(result + details);
            if (!ok || adminCheckFailed) logger.Warning(message);
            else logger.Info(message);
            ServerTranslationLog.Write(operation, session, ok && !adminCheckFailed ? "成功" : "失败", details);
            return response;
        }
        catch (Exception ex)
        {
            var reason = Clean(ServerLogText.Redact(ServerLogText.Failure(ex), request?.AdminKey), 400);
            ServerTranslationLog.Write(operation, session, "异常", reason);
            logger.Error($"[文本汉化][审计] {operation}失败 · 玩家：{player} · 会话摘要：{session} · 身份：{role}。{reason}");
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = "服务端操作失败，请管理员查看服务端日志与配置。"
            });
        }
    }

    internal static string ResponseDetails(string operation, JsonElement root)
    {
        var parts = new List<string>();
        if (operation == "校验管理员密钥" && root.TryGetProperty("isAdmin", out var isAdmin))
            parts.Add("管理员验证：" + (isAdmin.ValueKind == JsonValueKind.True ? "通过" : "未通过"));
        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            parts.Add("返回词条：" + entries.GetArrayLength());
        if (root.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Object)
            parts.Add("返回译文：" + values.EnumerateObject().Count());
        if (root.TryGetProperty("removedKeys", out var removed) && removed.ValueKind == JsonValueKind.Array)
            parts.Add("移除键：" + removed.GetArrayLength());
        var files = WrittenFilesDetails(root);
        if (files.Length != 0) parts.Add(files);
        if (root.TryGetProperty("jobId", out var jobId) && jobId.ValueKind == JsonValueKind.String)
            parts.Add("任务编号：" + Clean(jobId.GetString(), 40));
        foreach (var (property, label) in new[]
                 { ("saved", "已保存条数"), ("applied", "已应用条数"), ("files", "文件数"), ("retryAfterSeconds", "剩余等待秒数") })
            if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var number))
                parts.Add(label + "：" + number);
        foreach (var (property, label) in new[]
                 { ("changed", "有变更"), ("liveApplied", "已热应用"), ("restartRequired", "需重启") })
            if (root.TryGetProperty(property, out var value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                parts.Add(label + "：" + ServerLogText.YesNo(value.ValueKind == JsonValueKind.True));
        return parts.Count == 0 ? string.Empty : " · " + string.Join(" · ", parts);
    }

    internal static string WrittenFilesDetails(JsonElement root)
    {
        if (!root.TryGetProperty("activeFiles", out var activeFiles) || activeFiles.ValueKind != JsonValueKind.Array)
            return "";
        var count = activeFiles.GetArrayLength();
        if (count == 0) return "";
        var names = activeFiles.EnumerateArray().Where(file => file.ValueKind == JsonValueKind.String)
            .Take(3).Select(file => Clean(file.GetString(), 120));
        return $"写入 {count} 个译文文件（{string.Join("、", names)}{(count > 3 ? "等" : "")}）";
    }

    internal static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var clean = string.Concat(value.Select(character =>
            char.IsControl(character) || character is '\u2028' or '\u2029' ? ' ' : character)).Trim();
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }

    internal static bool HasProfile(SaveServer saveServer, string sessionId)
    {
        try { return saveServer.GetProfiles().Keys.Any(key => key.ToString().Equals(sessionId, StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    private static string ResolvePlayer(SaveServer saveServer, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return "未知";
        try
        {
            var profile = saveServer.GetProfiles().FirstOrDefault(pair =>
                string.Equals(pair.Key.ToString(), sessionId, StringComparison.OrdinalIgnoreCase)).Value;
            return Clean(profile?.ProfileInfo?.Username, 80) is { Length: > 0 } username ? username : "未知";
        }
        catch
        {
            return "未知";
        }
    }
}



