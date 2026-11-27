# AI 与开发者接手指南

本文面向在源码中维护 SimpleTranslatorCS、接入现有工作台、移植 SPT 版本或协助批量汉化的开发者与 AI。普通安装与操作见 [玩家与服主管理手册](PLAYER_GUIDE.zh-CN.md)。

## 开始前先确认

1. 记录当前分支、已有改动、目标 SPT 版本、游戏运行时及配套客户端提交。以工作区代码、实际程序集和当前测试为准。历史文档用于理解取舍，旧接口例子应以当前 DTO 重核。
2. 先读服务端入口与对应测试，再看客户端使用点。接口源于本项目，禁止假设存在公共浏览器 API、统一登录或生产服务地址。使用开发者持有、已获授权的测试 SPT 环境和测试档案。
3. 用户本机配置、译文、档案、真实会话 ID、管理员密钥与平台密钥留在原环境。反馈和提示词使用最小脱敏样例。离线单测与模拟供应商验证完成后，再由维护者决定是否测试真实付费接口。
4. 当前服务端源码版本 1.5.0，配套客户端和独立网页工作台为 2.14.1。协议版本数和 Mod 版本数各自管理。只有维护者指定后才调整发版号，详见 [版本号约定](VERSION_POLICY.zh-CN.md)。

## 源码、数据与产物

| 路径 | 从这里开始读 |
| --- | --- |
| `src/Startup/` | `SimpleTranslator.cs` 的元数据、DI 加载顺序、语言表挂载和颜色适配；`ModConfig.cs` 解析配置 |
| `src/Localization/` | `TranslationFiles` 发现与比较；`TranslationMerger` 覆盖和来源链；`Jsonc`；`ItemPlaceholders`；`DialogueLocalizer`；`LocaleDomainIndex`；`ServerLocaleOverlay` 和 `LocaleNotificationSocket` |
| `src/Workbench/` | `SimpleTranslatorRouter` 是路由和审计入口；`WorkbenchGateway` 检查会话和限流；`TranslationWorkbench` 管快照、基准、写入和发布；`WorkbenchContributions` 管候选与审核 |
| `src/Providers/` | `ServerTranslationExecution` 是请求 DTO、翻译校验、供应商调用和任务生命周期；`ServerProviderCatalog`、`ServerTaskPolicy` 管预设、权限与选项 |
| `src/Diagnostics/` | `ServerLogText` 中文显示和错误分类；`ServerJobDiagnostics` 结构化任务报告；`ServerTranslationLog` 操作/错误日志；`TranslationReport` 启动词条报告 |
| `Shared/` | 与客户端复用的请求状态、Token 预算、术语和模型规则。共享代码变更前后核对两边目标框架以及 `manifest.json` 的跟踪项和哈希 |
| `tests/`、`tools/` | SPT 多版本测试、离线编辑器、共享源码比对、打包脚本 |
| `docs/`、`glossary/`、`config.jsonc` | 用户文档、唯一可发布的术语示例、配置模板 |

普通译文新写入 `translations/ch/模组名_API.jsonc`、`模组名_MT.jsonc` 或 `模组名_Manual.jsonc`。文件名只从服务端的来源索引计算。旧分层和 `db/locales/ch` 文件原位兼容读取。`TranslationLayout` 统一分类、文件命名和合并优先级，存储缓存仍位于 `cache/workbench`。改动存储时同时验证批次拆分、重载一致性、旧生成词条的基准、回滚和人工文件开关。

仓库根的解决方案、项目和 `Directory.Build.props` 是构建入口。实际安装使用 `user/mods/Volcano-SimpleTranslator/`。构建产物 `dist*/`、发布包 `releases/`、参考 DLL 与本机运行数据应留在本地。兼容路径 `db/locales/ch` 与活动译文目录仍按运行时约定处理，将 C# 文件归入 `src/` 对这些路径没有影响。检查 Git 暂存内容，翻译文件如需随源码分发，需先确认许可、归属和使用目的。

## 协议速查

`src/Workbench/SimpleTranslatorRouter.cs` 定义当前路由，`src/Workbench/TranslationWorkbench.cs`、`WorkbenchContributions.cs` 和 `src/Providers/ServerTranslationExecution.cs` 定义载荷。配套客户端在相邻仓库 `src/Integration/ServerWorkbenchClient.cs`、`ServerLocaleSynchronizer.cs` 展示实际调用方式。

- 在正常 SPT 游戏会话里先检查原生 `/client/locale/ch` 的 Global 标记 `__SimpleTranslatorCS_WorkbenchProtocol`。识别后调用 `/simpletranslator/v1/capabilities`。当前 `protocolVersion` 为 2。按 `features` 协商功能；未识别时保持本地功能，不持续探测可选路由。
- 下表使用 `/simpletranslator/v1/workbench/` 前缀。HTTP 路由通过 SPT 当前版本的请求通道提交 JSON，身份从 SPT 会话传入。处理 JSON 的 `ok`、`unauthorized`、`conflict`、`rateLimited`、`jobMissing` 等业务状态。HTTP 返回成功仍可能携带业务错误。中文错误用于显示，代码按已有字段和状态分支。
- 管理路由提交 `adminKey`，校验使用服务端自己的密钥。玩家会话用服务端识别的有效档案。检测到 Fika 时当前工作台限制为管理员操作，`access` 的校验和 `live`/通知的只读路径按各自规则处理。扩展权限或撤销默认校验属于独立的设计变更。

| 路径后缀 | 输入与用途 | 角色 |
| --- | --- | --- |
| `access` | `adminKey` 可空；返回 `isAdmin`、`pullCooldownSeconds` | 正常会话或管理员 |
| `catalog`、`contribution-catalog` | 分别读取管理员目录与可贡献条目；使用 `revision`、条目 `id`、`english`、`sourceGroup`、文件状态 | 管理员目录需密钥；玩家目录受服务器策略约束 |
| `provider-status`、`models` | 先读取 `providerRevision` 与服务器设置；模型查询使用 `providerRevision`、`profileId`、`apiKey` 等，按权限过滤；允许手输模型 ID | 按会话、Fika 和模型权限 |
| `provider-test`、`job-start`、`contribution-start` | `mode` 为 `ai`/`machine`，并按需提交 `revision`、`providerRevision`、`profileId`、`sourceGroups`、`entryIds`、`options`、`machineOptions`、`apiKey`、`appId`、`requestId` | 管理任务需密钥；受允许的玩家任务只生成候选 |
| `job-status`、`job-cancel` | `jobId`；可用 `history` 读取近期任务；新请求未确认时按客户端使用方式处理 `stopPending` | 管理员或对应候选任务的发起人 |
| `save` | `revision`、`mode`、`entries[].{id,source,translation,keepOriginal}`。按来源写入，`activeFiles` 列出本批所有相对文件名，`activeFile` 保留首个文件名供旧客户端使用 | 管理员 |
| `manual-submit`、`web-player-submit`、`contribution-receipt` | 按 DTO 使用条目与 `submissionId`，网页玩家路径只入候选，回执按所有者查询 | 受服务器玩家/管理员策略约束 |
| `contribution-pending`、`contribution-decision`、`review`、`review-save` | 审阅待处理记录、批准/拒绝及变更现有译文，保存时重新校验 | 管理员 |
| `files`、`baseline`、`reload`、`pull` | 开关文件、更新基准、重载或主动拉取；`files` 的 `path` 使用目录返回的相对路径，旧文件 ID 可带 `@legacy/` | 写操作需密钥；手动拉取另有冷却和 Fika 策略 |
| `live` | 合法已知会话提供 `liveRevision`，读取 `changed`、`values`、`removedKeys`、`restartRequired` | 只读 |
| `locale-notify` | WebSocket 使用 `X-SimpleTranslator-Session` 和有效档案，收到版本字符串后再用 `live` 读取内容 | 只读 |

流程示意：确认能力 → 验证身份 → 读取目录和供应商修订 → 选取当前仍缺失的条目 → 小批启动 → 跟踪任务 → 校对或审核 → 按发布版本读取同步。采用客户端已有的 SPT 通道、协议和存储层。请求示例可从测试读取，所有凭据使用虚构样例。

管理员示例仅说明载荷结构。开始前应填写当前服务器返回的修订和条目，而非复用旧运行目录中的结果。

```json
{
  "mode": "ai",
  "revision": "<当前目录 revision>",
  "providerRevision": "<当前供应商 providerRevision>",
  "profileId": "default",
  "entryIds": ["<目录返回的真实测试键>"],
  "requestId": "<没有短横线的 32 位 GUID>",
  "adminKey": "<从测试服会话安全传入>",
  "apiKey": "<测试供应商凭据>"
}
```

`requestId` 用于关联一次启动及其近期状态。启动响应丢失时先查询或按客户端机制复用原 ID，避免另起重复可计费任务。对 `conflict` 重新获取目录并比较原文和待翻资格，再决定是否提交。HTTP 工作台回填上限为 1000 条、原文与译文合计 500000 字符，实际以路由和条目限制为准。禁止从目录取不到就猜 ID 或遍历真实玩家资产。

## 批量汉化操作清单

1. **确定来源。** 普通键优先从加载中的 Global 语言表和工作台实体目录获取。当前常见域是物品、任务及条件、成就、商人、外观和手册。打开 `includeOtherLocaleText` 时，获准的其他语言键单独归类。对话文件、游戏硬编码 UI 和 F12 设置各用自己的流程。
2. **保留身份。** 草稿数据记录稳定 ID、精确英文原文、来源和域。批处理时按原 ID 返回一对一结果，不按数组位置猜测身份。用户授权的文本可按源组和上下文拆批；资料来源不能证明就提交小样例复核。
3. **保留标记。** 保留 `{0}`、`%s`、自定义物品模板 `${24位十六进制物品ID}` 及 `${24位十六进制物品ID|short}`、富文本标签、路径、快捷键、数字和换行。AI 输出只提取所需的 JSON，拒绝增删或编造条目。语义用词以术语表辅助并经人工校对。
4. **校验和提交。** 用 `ServerTranslationValidation.Validate` 与现有保存边界复查。模型结果如果只是复述原文会被拒；人工确认要保留的名称可在支持的人工流程明确标记 `keepOriginal`。当原文、目录修订、待审项或缺失资格变化时，刷新并只处理仍有效的条目。
5. **预算与续翻。** 优先跳过已有或待审项，复用同任务等价结果。小批、限制并发和请求量，只按已有策略处理 429、5xx 和 413。任务输出检查点与回填原文基准具备不同用途，按现有路径保存。超时、断网或输出格式异常先排查再决定后续请求。
6. **验证。** 测试普通译文和对话、没有命中的术语、占位符异常、同键竞争、取消、重新连接和角色隔离。日志与报告脱敏后再分享，不发送原始真人聊天、档案或完整本机字典。

## 移植及升级清单

| 目标 | 框架与接口 |
| --- | --- |
| SPT 4.0.x | .NET 9，4.0 系列 NuGet，`SPT40`、`OnLoad()`、`DatabaseServer.GetTables()`，原生 SPT 日志颜色枚举 |
| SPT 4.1.x | .NET 10，4.1 系列 NuGet，`OnLoadAsync`、注入本地化和模板表、Spectre 日志颜色 |
| SPT 5.0 BE | .NET 10，`SPT50`、匹配目标 BE 的本机程序集；检查 route 回调、WebSocket 事件签名、对话键类型 |

移植先补本地程序集路径和编译适配，再按测试覆盖实际运行时。确认 Mod 元数据的版本区间、注入顺序、Global 比较器、对话 ID、`LazyLoad` 缓存能力、原版和第三方 Mod 的合并顺序。必须验证应用、撤销、缓存失效及重放，再检查客户端的发现标记、请求 DTO 与主线程刷新。用能力协商新增行为；线上版本还依赖的字段、状态和路由应继续兼容。颜色适配放在启动日志入口，网络标识和日志 JSON 字段继续用稳定名称。

从源码仓库根运行：

```powershell
dotnet test tests/SimpleTranslatorCS.Tests/SimpleTranslatorCS.Tests.csproj -c Release -p:SptTarget=4.0
dotnet test tests/SimpleTranslatorCS.Tests/SimpleTranslatorCS.Tests.csproj -c Release -p:SptTarget=4.1
dotnet test tests/SimpleTranslatorCS.Tests/SimpleTranslatorCS.Tests.csproj -c Release -p:SptTarget=5.0be "-p:Spt50RuntimePath=<本机 SPT_Runtime 目录>"
./tools/Verify-Foundation.ps1
node tests/glossary-editor.test.cjs
python tools/package_release.py --server-only --output releases/2026-11-26-server
```

构建或测试顺序执行三个目标。4.0、4.1、5.0 BE 的对应输出目录是 `dist-4.0/`、`dist/`、`dist-5.0be/`。服务端独立打包要求三个目标均已构建；同时打包客户端时保留相邻客户端仓库并提前构建 Mono 和 IL2CPP。发布脚本检查文档链接、ZIP 完整性和敏感文件，并生成 `SHA256SUMS.txt`。浏览器或控制台表现仍要在目标宿主检查，测试通过只证明所覆盖的场景。

发布前审查暂存区，只发布核准的源码和资源。不把活动术语表、译文缓存、管理员配置、运行日志和本机引用 DLL 放进 GitHub 或安装 ZIP。README 的安装示例以及 ZIP 内的 `README.zh-CN.md` 应能在全新安装中使用。

## 给接手 AI 的输出要求

说明你读过的入口、当前实现、触及的目标、实际改动、执行过的命令和未覆盖的场景。把失效的旧文档与最新源码区分开。提交可复现样例和最小补丁，保留现有未提交工作。假供应商、脱敏输入、受控测试服足以验证大多数路径，涉及真实付费、发布或生产环境时交给维护者授权。
