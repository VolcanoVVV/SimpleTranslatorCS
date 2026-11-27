# 1.13.0 / 2.13.0：模型获取、可选组件探测与发布

当前旧路径读取、热同步日志及术语表行为以 [本轮补充说明](LEGACY_GLOSSARY_QUIET_SYNC_2026-10-07.zh-CN.md) 为准。

## 自动获取模型

ConfigurationManager.zh-cn 的 AI 页保留「模型 ID」输入框，增加自动获取开关、模型列表、关键词筛选及「刷新模型」。填写密钥、停止输入约两秒后查询当前服务商接口；默认十分钟缓存，失败缓存一分钟，手动刷新最短间隔五秒。关闭自动获取后仍可点击刷新。列表不会自动替换现有模型；切换接口或密钥后重新查询，旧请求的结果不覆盖新接口列表。

查询从 Chat Completions 完整地址推导同源 `/models`，仅 GET 模型元数据，不发送提示词、游戏文本或生成请求。OpenAI、DeepSeek、Gemini OpenAI 兼容接口、Ollama 等提供此协议；自定义代理和其他供应商是否提供此接口取决于服务本身。模型列表不是翻译能力或免费额度的证明，账户实际授权、模型类型及服务商计费规则仍以供应商为准。模型最多显示 500 个，界面每页最多展示 20 项，可按关键词筛选；接口分页中的后续条目、不开放列表的接口和未收录的新模型均可手动输入。

官方协议参考：[OpenAI Models](https://developers.openai.com/api/reference/resources/models/methods/list)、[DeepSeek Models](https://api-docs.deepseek.com/api/list-models/)、[Gemini 兼容接口](https://ai.google.dev/gemini-api/docs/openai#list_models)、[Ollama 兼容接口](https://docs.ollama.com/api/openai-compatibility)。

服务端 AI 高级选项也能查询模型。请求只包含已批准的预设 ID、配置指纹和本人的密钥；客户端不能指定查询目的 URL。配置变化后不向旧接口发送密钥。玩家列表与 `model`、`allowedModels` 取交集，手动输入同样接受服务端校验；管理员可在本次任务输入新模型，但不会改写管理员预设文件。要开放给玩家，应由管理员将模型加入服务端 `translation-providers.jsonc` 的 `allowedModels`。不支持查询的旧服务端不会收到新增查询路由请求。

所有模型请求禁止 HTTP 重定向、有二十秒超时及四 MiB 响应上限；远程地址须 HTTPS，本机模型可 HTTP。缓存按接口和密钥摘要隔离、只在内存保存；服务端网关沿用档案、管理员密钥和限流规则，不凭 Fika 房主身份赋予写权限。

## 只安装客户端

2026-10-07 校正：先前请求误用了菜单语言表，已改为游戏文本表；详见 [同版本修正说明](SERVER_WORKBENCH_DISCOVERY_FIX_2026-10-07.zh-CN.md)。

客户端先读取 SPT 原生 `/client/locale/ch`，检查服务端加入的专用协议标记；没有有效标记时隐藏服务端工作台，不请求 `/simpletranslator/v1/capabilities`，也不触发该缺失路由的自动重试。F12 人工、API 与网页回填仍可独立使用。老版本服务端没有此标记，需要配套升级到 1.13.0；这一设计以避免对不存在的可选组件发起探测为优先。

服务器或会话切换会清空服务端凭据、重新发现组件。标记只声明组件存在；写操作仍经过独立的权限验证。

## 日志位置与内容

```text
游戏根目录/
└── API翻译日志/
    ├── ConfigurationManager.zh-cn/
    │   ├── 时间-任务ID.jsonl        # 任务、进度、结果、token 与错误
    │   ├── 时间-任务ID.inputs.jsonl # 输入原文与提示词
    │   ├── 日期.errors.jsonl       # 每日错误索引
    │   └── 日期.operations.jsonl   # 操作记录
    └── SimpleTranslatorCS/
        ├── 任务ID.json             # 持续更新的服务端任务快照
        ├── 任务ID.inputs.jsonl     # 输入原文与提示词
        ├── 日期.errors.jsonl       # 每日错误索引
        └── 日期.operations.jsonl   # 权限与工作台操作审计
```

客户端按 BepInEx 目录定位游戏根目录，服务器按 `user/mods` 定位，并识别 SPT/SPT_Runtime 外面的游戏目录。独立服务端没有游戏目录时放在服务器根目录。远程玩家在自己的游戏目录看客户端日志，服务端日志在运营者机器；输入日志不通过任务状态接口公开给其他玩家。

输入日志包含待翻译原文和 AI 提示词，便于排查译法；进度与错误文件单独记录。API 密钥、应用密钥和管理员密钥脱敏，不记录 HTTP 请求头或供应商原始错误正文。token 仅统计供应商实际返回的 usage。旧 `cache/diagnostics` 日志保留原处作为历史记录，新任务只写新目录；基准、缓存译文、候选审核等功能数据仍保留在原存储位置。

## 安装包与维护

发布包位于 SimpleTranslatorCS 项目的 `releases/`，附带 `SHA256SUMS.txt`。Mono 对应 SPT 4.x 的 BepInEx 5；IL2CPP 对应 5.0 BE 的 BepInEx 6。每台客户端只能安装适合其运行时的一个 DLL，两个包不是同时安装。需要预先安装对应运行时的 ConfigurationManager；IL2CPP 包包含所需 Newtonsoft.Json 开源依赖。

客户端包解压到游戏根目录。服务端包解压到含 `user/mods` 的服务端根目录：本机 4.0 是游戏下 `SPT`，4.1/5.0 是 `SPT_Runtime`。服务端包目录沿用 `user/mods/Volcano-SimpleTranslator`；若旧安装使用不同文件夹名，应覆盖原目录，避免加载两份 DLL。升级前保留已有配置和自定义词典。

发布只包含指定 DLL、默认 UI 词典、默认配置、空译文目录和说明；不包含本机译文、账号密钥、任务缓存、日志或游戏程序集。`tools/package_release.py` 明确列举文件并检查 ZIP 完整性和私有文件排除。公共模型与路径逻辑使用 C# 7.3，两个项目 vendored 内容由 `Shared/manifest.json` 和 `tools/Verify-Foundation.ps1` 校验一致。

版本验证记录：三个服务端目标各 110 项测试，客户端 223 个回归断言；Mono 4.0/4.1 与 IL2CPP 分别构建，布局边界检查和 IL2CPP API 冒烟检查通过。使用本地模拟供应商，没有消费用户 token。实际 Fika 联机和游戏内画面仍需实机验收；5.0 BE 后续上游变更需按矩阵重新编译验证。
