# 三项目现状与复用评估

检查日期：2026-09-19。本文描述检查时源码，不承诺服务商预设地址和模型在未来仍可用。产品设计见 [集成设计](INTEGRATION_DESIGN.zh-CN.md)。

## 1. 项目位置与运行时

| 项目 | 本机代码目录 | 运行时与依赖 |
| --- | --- | --- |
| SimpleTranslatorCS | `V:\EFToffline\DeveloperMod\SimpleTranslatorCS` | 4.0 构建 net9.0，4.1 构建 net10.0；引用 SPT 4.0.9 / 4.1.2 |
| ConfigurationManager.zh-cn | `V:\EFToffline\DeveloperMod\ConfigurationManager.zh-cn` | .NET Framework 4.7.1、C# 7.3、BepInEx、Harmony、Unity IMGUI、Newtonsoft.Json；插件源码版本 2.3.1 |
| ID 提取工具 | `V:\EFToffline\DeveloperMod\ID提取工具` | Core/CLI net8.0；GUI net8.0-windows + WPF |

本机 SPT 实际 Core 分别为 4.0.13 / 4.1.3。客户端 zh-cn 的工程引用目前指向 4.1debug，不能仅凭服务器双版本构建就声称客户端也同时兼容。

没有在这两个目标目录内发现适用的 AGENTS.md。两目录不在当前任务的可写根内；本轮只读它们。后续修改前需将对应目录纳入任务可写范围，或依环境规则申请写入权限。

## 2. ConfigurationManager.zh-cn

### 已有能力

- `ConfigurationManagerZhCnPlugin.cs:17`：BepInEx 插件，硬依赖 ConfigurationManager；收集并修改配置显示元数据。
- `LocalizationModels.cs:21,111`：LocalizationSnapshot / TranslationWorkItem，当前模型围绕 Plugin、Setting、Enum、Custom 配置文本。
- `TranslationPanel.cs:15,81`：静态 IMGUI 工作台，已有手动、AI、机翻、范围、更新、外观页；不是已公开的第三方工作台扩展 SDK。
- `TranslationBatch.cs`：批量计划、已有译文跳过、逐插件处理、进度、失败继续、取消。单插件未完整成功不保存；已成功插件保留。
- `TranslationClients.cs:78`：异步 AI 调用、相同语境去重、请求指纹缓存、超时及取消；机翻有多个专用协议适配。
- `TranslationSafety.cs`：结构化响应校验、占位符/标签/数字/快捷键/换行检查、语境和提示词指纹。
- `TranslationTracking.cs`：按层记录原文基准、过期检测、不同译文层独立确认。
- `JsoncLocalization.cs:247,445`：Manual/AI sidecar、原文件保留、历史归档、原子写入；Machine 仅内存临时层。
- `JsoncLocalization.cs:582`：文件时间变化检查及文档缓存失效。
- `TranslationPanel.cs:1290` 附近：RefreshManagerIfRequested 反射调用 **ConfigurationManager.BuildSettingList**。

### 可以直接沿用的设计

人工优先、AI 初译、临时机翻分层；设置变动只影响下一次任务；每任务固定目标；基准哈希判断过期；人工和 AI 分层确认；历史和备份；密钥默认只在当前进程内存；坏文件阻止覆盖；进度与取消机制。

### 需要重构的边界

1. 工作台目前直接依赖 JsoncLocalization、BepInEx 配置快照和静态 UI 状态。先抽出数据源/任务/存储/刷新接口，再添加服务端文本页，不能把游戏 locale 伪装成 ConfigEntries。
2. 当前刷新能力不涉及 EFT 物品、任务、Dialogue 缓存，也不能把现有鼠标抓取当成任意游戏 UI 的文本定位器。IMGUI 配置控件抓取与游戏其他 UI 系统不同。
3. 当前 AI 分支统一构造 Chat Completions 请求并读取 choices.message.content；即使某预设额外添加其他鉴权头，也不等于实现该平台原生消息协议。
4. 大部分复用类是 internal，且保存逻辑依赖全局快照。其他插件没有现成的稳定注册入口。
5. 配置界面的短标签与任务叙事文本需要不同提示词和校验策略，不能原样复用“配置界面本地化”提示词。

### 已有测试基础

`tests/Program.cs` 是独立回归程序，使用模拟 HTTP 和临时文件；覆盖缓存、签名、保护标记、历史、逐层基准和任务等。`tests/RegressionTests.csproj` 运行于 net8.0、引用 Release DLL。它不是 Unity Mono 真实加载测试，也不是新服务端模块集成测试。本轮未执行。

## 3. ID 提取工具

### 已有能力

- `src/SptIdExtractor.Core/SptIdExtractor.Core.csproj`：独立 net8.0 库，未依赖 WPF/Unity/SPT 类型；是适合复用的起点。
- `LocaleScanner.cs:28,37`：ScanAsync / Scan，目录或 JSON/JSONC 扫描，取消，文件大小限制。
- `LocaleScanner.cs:21`：使用 System.Text.Json 原生注释和尾逗号支持，可参考修复 SimpleTranslatorCS 的正则解析缺陷。
- `Models.cs`：类别、置信度、文件/JSON 路径、规则、冲突与备选来源，具有可审查的提取结果。
- `LocaleScanner.cs:755`：按候选优先级、文件、JSON 路径稳定选择，保留备选；同级不同值标记冲突。
- `OutputWriter.cs:31`：输出普通 locale 与 dialogue 子目录，支持替换值后导出扁平 JSONC。
- `OpenAiCompatibleTranslator.cs:43`：按选中条目翻译，条目数/字符量分批、超时、取消、响应校验。
- `TranslationModels.cs` / `OpenAiCompatibleTranslator.cs:259`：已有 Chat Completions、Anthropic Messages、Gemini GenerateContent 三套协议分支，且有模型列表接口。
- GUI/CLI 已经串联扫描、自动翻译和导出；并非需要从零开发自动初译。

### 直接集成前必须处理

1. **全局裸 key 合并。**MergeCandidates 只按 Key 分组；扫描多个 mod、普通 locale 和 Dialogue 时可能丢失不同作用域的来源。TranslationResult 也只按 key 返回。集成模型需要拆开“候选记录身份”和“实际应用位置”。
2. **缺少明确的 mod 身份。**目前来源主要是路径，不能直接作为服务端已加载 mod GUID 的可靠替代。需要扫描根 → 模组候选身份 → 运行时元数据核对。
3. **静态扫描边界。**不运行 mod、不反编译 DLL，不直接读压缩包；运行时生成的文本不能由此完整提取。缺少文本不是自动翻译失败，更不能凭 ID 编造原文。
4. **输出不是跨文件事务。**WriteLocaleFiles 顺序直接写普通文件和 dialogue 文件；翻译成功后发生磁盘错误仍可能只写出一部分。在线发布不能直接复用这个持久化步骤。
5. **valueOverrides 有原文回退。**缺失译文时 OutputWriter 会写原文；在线提交应明确 reject/partial-draft 策略，不能把遗漏误报为全部成功。
6. **冲突不自动排除。**Selected 默认 true；翻译器过滤的是 Selected 和非空值，HasConflict 并非通用执行前置条件。自动初译默认应排除未解决冲突和低置信度项。
7. **保护标记标准不同。**提取器校验器较侧重占位符/标签；工作台还保护数字、快捷键和换行。SimpleTranslator 的 `${id}` / `${id|short}` 必须作为完整标记保护，不能仅匹配其中 `{...}` 而漏掉 `$`。需要新增专门用例。
8. **错误信息脱敏不同。**提取器 SendWithRetryAsync 会把最多 1200 字符的服务商响应体放入异常；工作台有意不显示响应体。共享实现应先统一脱敏，避免服务商回显密钥或原文后进入日志。
9. **翻译请求上下文不足。**当前 SourceText 只携带 Key/Value，未利用类别、来源和相邻文本。可以升级为带简短语境的请求，但不得发送配置值、存档或整个文件。

### 已有测试与记录

`tests/SptIdExtractor.Core.Tests/TranslationTests.cs` 有三协议模拟响应、模型列表、标记校验及导出测试；`VALIDATION.md` 有历史提取样本和缺失样本记录。本轮只读检查，没有新跑完整样本验证。

## 4. 跨项目复用的现实约束

| 复用目标 | 判断 |
| --- | --- |
| Core 扫描器 → 服务端 net9/net10 | 框架方向可行；仍要验证依赖、SPT 加载器和打包 |
| Core net8 DLL → zh-cn net471/Unity Mono | 不能直接引用运行；需要移植公共子集或通过协议调用 |
| WPF GUI → 游戏内工作台 | 不能直接嵌入，应复用结果模型和工作流 |
| zh-cn 配置 sidecar → SPT locale 覆盖文件 | schema 不同，需适配器；不应强行合并格式 |
| zh-cn 界面刷新 → EFT locale 刷新 | 当前不可直接复用，新增客户端桥接 |
| 共享提供商协议实现 | 有价值，但先分离 UI/数据模型并验证 Mono 依赖 |

微软对 .NET Framework 与现代 .NET 的公共库建议是 .NET Standard 2.0；该规范可作为小型公共库的候选目标，而不是直接把整个 Core 改目标就完成迁移。[官方说明](https://learn.microsoft.com/en-us/dotnet/standard/net-standard)

现有 GeneratedRegex、现代框架 API、序列化依赖及 Unity 已加载程序集版本都需要处理。建议先以版本化 JSON DTO 连接边界；再抽出无 Unity/SPT/WPF 依赖的翻译任务、校验、指纹及提供商公共子集。net471 工程在真实 Unity Mono 环境中的兼容性仍需独立验收。

SimpleTranslatorCS 当前 CopyToDist 只明确复制主 DLL、config 和翻译文件。引入 Core 或公共库后必须更新依赖打包及重复程序集加载验证，否则“本地编译成功”不代表部署可运行。
