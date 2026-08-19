# SimpleTranslatorCS

SPT 4.0 / 4.1 服务器端文本汉化 Mod。扫描 `db/locales/ch/` 下的 `.json` / `.jsonc` 翻译文件，在服务器启动时将文本覆盖到中文（ch）本地化数据库中，并输出覆盖率统计日志。

- 作者：MiKueYu & Volcano
- 适配版本：SPT ~4.1.0 / ~4.0.0（双版本构建，见下文）
- 许可：MIT

## 目录结构

```
SimpleTranslatorCS/
├── db/locales/ch/            # 普通本地化文本（商人、物品、任务、界面等）
│   ├── 【模组名】~版本-作者.jsonc
│   └── ...
└── db/locales/ch/dialogue/   # 商人对话文本（单独处理，见下文）
```

- 支持 `.json` 和 `.jsonc`（允许注释和尾随逗号），推荐使用 `.jsonc`。
- 一个模组对应一个翻译文件，文件名建议格式：`【模组名】~适配版本-汉化作者.jsonc`，例如 `【RaidOverhual 战局大修】~2.7.1-Volcano.jsonc`。
- `dialogue/` 子目录下的文件不会被当作普通本地化加载，而是用于覆盖商人对话元素的文本，且只有对话元素中已存在的文本 ID 才会被更新，不会新增键。

## JSONC 书写规范

翻译文件是**扁平的键值对**（不支持嵌套对象），键为游戏 locale 键，值为中文文本。标准模板如下：

```jsonc
{
 /*
 ** [模组名称] RaidOverhual - 战局大修
 ** [模组网址] https://hub.sp-tarkov.com/files/file/1673-raid-overhaul/
 ** [时间版本] 2025-4-29 ~2.7.1
 ** [汉化作者] Volcano https://sns.oddba.cn/author/8393
 */

    /* [商人名称] */
    "66f0eaa93f6cc015bc1f3acb Nickname": "需求事务所",
    "66f0eaa93f6cc015bc1f3acb Location": "Kamchatskaya街拱门附近的一所废弃学校",
    "66f0eaa93f6cc015bc1f3acb Description": "一群 前PMC 和 叛变Scav 组成了一个组织……",

    /* [商人物品] */
    "66292e79a4d9da25e683ab55 Name": "需求补给表",
    "66292e79a4d9da25e683ab55 ShortName": "需求表",
    "66292e79a4d9da25e683ab55 Description": "用于与需求事务所进行交易的需求表……",

    /* [商人任务] */
    "66f0ecad7d6d9736b17c2167 name": "缝缝补补: Kappa安全箱升级",
    "66f0ecad7d6d9736b17c2167 description": "这就是我一直在说的！……",
    "66f0ecad7d6d9736b17c2167 successMessageText": "我不喜欢装逼过分吹嘘自己，但这真是一件艺术品。……",

    /* [任务目标] */
    "672bf390e2c571e9f95c1be4": "上交 电钻",
    "672bf39152047176b40f3189": "上交 电动马达",
    "672bf397f564c404b2f88c48": "上交 需求补给单"
}
```

书写要求：

- **文件头注释**：用块注释标注 `[模组名称]`、`[模组网址]`、`[时间版本]`、`[汉化作者]` 四项，方便溯源与维护。
- **分区注释**：用 `/* [分区名] */` 划分内容，常用分区：`[商人名称]`、`[商人物品]`、`[商人任务]`、`[任务目标]`。
- **键值格式**：每行一对 `"键": "值",`，键和值都必须用英文双引号包裹；值内部的英文双引号需转义为 `\"`，换行用 `\n`。
- **顶层结构**：整个文件只能有一个根对象 `{ }`，内部不得再嵌套对象或数组。
- **宽松语法**：支持 `/* */` 块注释、`//` 行注释和尾随逗号（仅 `.jsonc`；`.json` 文件请保持严格格式）。

常见键名约定：

| 键格式 | 含义 |
| --- | --- |
| `{商人id} Nickname` / `Location` / `Description` | 商人名称 / 位置 / 简介 |
| `{物品id} Name` / `ShortName` / `Description` | 物品全名 / 短名 / 描述 |
| `{任务id} name` / `description` / `successMessageText` | 任务名 / 任务描述 / 完成寄语 |
| `{24位id}`（无前缀后缀） | 任务目标等独立文本条目 |

## 物品名占位符 `${mongoId}`

任务目标等文本中经常需要引用物品名称。直接写死物品名会在物品 Mod 改名后过时，裸写 mongo id 又会在游戏里显示为一串十六进制。为此翻译文本支持占位符语法，加载时会被自动替换为对应语言的物品名称：

```jsonc
{
    // ${24位mongoId} —— 替换为物品全名（locale 键 "{id} Name"）
    "b5d786ccd0d59bfb69d00342": "上交 15 ${4012db58df2b5c63e64cc79f}",

    // ${24位mongoId|short} —— 替换为物品短名（locale 键 "{id} ShortName"）
    "95e66a8359db201572928525": "上交 20 ${670f21e143b5fb2f15e44585|short}"
}
```

解析规则：

- 查找顺序：中文字典（含其他 Mod 追加的物品名）→ 英文字典。
- 都找不到（id 无效或对应物品 Mod 未安装）时回退为裸 id 文本，不会残留 `${...}` 符号，也不会报错。
- 普通本地化文件和 `dialogue/` 文件均支持该语法。
- 占位符解析在 locale 字典构建时（transformer 阶段）执行，因此其他 Mod 新增的物品也能正确解析。

## 设置（config.jsonc）

Mod 根目录下的 `config.jsonc` 提供开发/日志模式，默认关闭：

```jsonc
{
    // 开发/日志模式：true 时，加载完成后导出词条生效报告
    "devMode": false,
    // 报告输出文件名（相对 mod 目录）
    "reportFile": "translation-report.jsonc"
}
```

开启 `devMode` 后，服务器启动加载完所有翻译，会在 mod 目录生成报告 jsonc（带 BOM 的 UTF-8，中文原样输出不转义，记事本可直接打开），按文件分组、逐条用注释标记状态：

```jsonc
    /* [文件] 【RaidOverhual 战局大修】~2.7.1-Volcano.jsonc (生效 12/13) */
    // [生效]
    "66f0eaa93f6cc015bc1f3acb Nickname": "需求事务所",
    // [未生效]
    "bad-key": "这条不会显示",
```

生效判定：

- **普通本地化**：键在英文或中文现有字典中存在（覆盖到了游戏原文）。
- **Dialogue**：键匹配到了至少一个对话元素。
- `[未生效]` 的词条不会显示在游戏里——通常是键名拼写错误，或对应 Mod 未安装。文件头部的 `(生效 n/m)` 即"写了一半"的部分覆盖情况。

报告末尾还有两个排查章节（仅在有问题时出现）：

- **键冲突**：同一个键被多个翻译文件覆盖（后者覆盖前者），逐条列出涉及的文件。
- **占位符解析失败**：`${mongoId}` 找不到物品名的条目（物品不存在或对应 Mod 未安装）。该章节基于加载时字典预览，mod 物品可能误报，准确结果以运行时日志中的警告为准。

另外开启 `devMode` 后还会**监听翻译目录的文件变更**，编辑器保存后服务器日志会提示"重启后生效"（locale 字典构建后即固化，无法真正热重载）。

与配置无关、始终生效的两类警告：

- **同文件内重复键**：保留最后一条并逐键警告（System.Text.Json 默认静默覆盖，容易漏看）。
- **编码问题**：文件不是 UTF-8（常见于记事本存出的 GBK/ANSI）时给出明确警告，请另存为 UTF-8。

## 对 Mod 开发者

源码按职责拆分，纯逻辑类不依赖 SPT 类型（日志走回调），可直接单元测试：

| 文件 | 职责 |
| --- | --- |
| `SimpleTranslator.cs` | ModMetadata、主加载流程、统计日志、文件监听 |
| `Jsonc.cs` | JSONC 注释剥离 / 尾随逗号 / 文件解析（重复键、编码检测） |
| `ItemPlaceholders.cs` | `${mongoId}` 占位符解析 |
| `DialogueLocalizer.cs` | dialogue 词条写入（键并集预过滤优化） |
| `ModConfig.cs` | `config.jsonc` 加载 |
| `TranslationReport.cs` | 开发模式报告（生效状态 / 键冲突 / 占位符失败） |

- 普通本地化的写入通过 `localeTable.Global["ch"].AddTransformer` 延迟合并：第一遍写入全部条目，第二遍解析占位符，因此翻译文件自带的 `"{id} Name"` 条目也能被同文件内的占位符引用。
- dialogue 文本在 `OnLoad` 阶段直接写入对话数据表，占位符解析使用 `LocaleService.GetLocaleDb` 的当前字典（ch 优先、en 兜底）。
- 版本号单一来源是 csproj 的 `<Version>`，`ModMetadata` 从程序集元数据读回，发版只需改 csproj。
- 测试：`dotnet test tests/SimpleTranslatorCS.Tests`（MSTest，覆盖 Jsonc / ItemPlaceholders / ModConfig / TranslationReport）。
- CI：`.github/workflows/build.yml` 跑测试 + 四档构建并上传 dist 产物。

### 双版本构建（4.0.x / 4.1.x）

同一份源码可同时面向 SPT 4.1.x 和 4.0.x 构建。VS 配置管理器里提供四档配置，命令行等价写法如下：

| 配置 | 面向 | 命令行 | 产物目录 |
| --- | --- | --- | --- |
| `4.1 Release` | SPT 4.1.x (net10) | `dotnet build -c "4.1 Release"` | `dist/` |
| `4.1 Debug` | SPT 4.1.x (net10) | `dotnet build -c "4.1 Debug"` | `dist-debug/` |
| `4.0 Release` | SPT 4.0.x (net9) | `dotnet build -c "4.0 Release"` | `dist-4.0/` |
| `4.0 Debug` | SPT 4.0.x (net9) | `dotnet build -c "4.0 Debug"` | `dist-4.0-debug/` |

四档的区别在两个维度：**SptTarget**（4.1 用 NuGet 包 4.1.2 + net10，4.0 用 NuGet 包 4.0.9 + net9）和 **Debug/Release**（Debug = 不优化 + 完整调试符号 + 定义 `DEBUG`；Release = 开优化，用于发布）。Debug 档的发布目录带 `-debug` 后缀，防止调试版 dll 覆盖发布版。普通的 `Debug`/`Release` 配置等价于对应的 4.1 档；也可用 `-p:SptTarget=4.0` 显式指定目标（推导规则见 `Directory.Build.props`）。

- 4.0 构建会定义条件编译符号 `SPT40`，`SimpleTranslator.cs` 中的 `#if SPT40` 块负责适配两个版本的 API 差异：元数据（`AbstractModMetadata` vs `IModMetadata`）、`IOnLoad.OnLoad()` vs `OnLoadAsync(CancellationToken)`、`OnLoadOrder.PostDBModLoader` vs `PostLoad`、`DatabaseServer.GetTables()` vs 注入的 `LocaleTable`/`TemplateTable`、日志颜色枚举（`LogTextColor` vs `Spectre.Console.Color`）等。
- **加载顺序刻意放到最后**：翻译 mod 必须在被翻译 mod 之后执行（dialogue 是启动时直写，错过了就补不上；transformer 也是后注册者生效）。4.0 用 `OnLoadOrder.PostSptModLoader + 1`，4.1 用 `OnLoadOrder.PostLoad + 1`（`+1` 是为了排在同档其他 mod 之后）。注意两版档位名含义不同：4.1 的 `PostLoad` 是所有 mod 默认所在档，4.0 里对应位置叫 `PostDBModLoader`。
- **TargetFramework 也随版本切换**：SPT 4.1 服务器运行在 .NET 10，SPT 4.0 运行在 .NET 9，因此 4.0 构建为 `net9.0`、4.1 构建为 `net10.0`。用 net10 编出的 dll 放进 4.0 服务器会报 `System.Runtime, Version=10.0.0.0` 找不到。
- 两个版本的 `obj`/`bin`/`dist` 目录互相隔离，可交替构建而无需清理。
- 4.0 依赖自 1.4.0 起改用 NuGet 包（4.0.9，为 4.0 线最新补丁版；所用 API 已在 4.0.0 验证存在），`tref/` 目录的本地 dll 不再参与构建，仅作存档参考。
