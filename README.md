# SimpleTranslatorCS

SimpleTranslatorCS 是 SPT 服务端中文覆盖工具。把物品、任务、商人等译文放进 Mod 目录，服务器就能在读取中文语言表时覆盖对应键。仅使用已有译文时，安装服务端即可。需要在游戏内扫描、翻译、投稿或校对时，可使用 [ConfigurationManager.zh-cn 2.14.1](https://github.com/VolcanoVVV/ConfigurationManager.zh-cn/releases/tag/2.14.1) 中的 F12 或独立网页工作台。网页直连支持没有游戏客户端的服务器管理。

当前服务端版本为 **1.5.0**，分别构建 SPT **4.0.x、4.1.x、5.0 BE** 安装包。5.0 BE 包针对构建时使用的测试版运行时，不保证适用于后续 BE 或正式 5.0。

## 快速开始

1. 选择与服务器匹配的 ZIP。停止服务端，将包内的 `user` 文件夹解压到包含 `user/mods` 的 **SPT 服务端根目录**。
2. 确认 DLL 位于 `user/mods/Volcano-SimpleTranslator/SimpleTranslatorCS.dll`。将普通译文放进同目录下的 `translations/ch/`。工作台按来源保存 `模组名_API.jsonc`、`模组名_MT.jsonc` 或 `模组名_Manual.jsonc`。旧版子目录和 `db/locales/ch/` 仍可原位读取。
3. 启动服务端，搜索 `文本汉化`。绿色显示已有键覆盖和语言表写入，青色显示新增键，黄色提示部分覆盖或同键竞争。错误详情会指出文件、权限或数据格式；JSON 错误附带可用的行号及字节位置。
4. F12 用户进入 **汉化工作台 → 服务端游戏文本**。使用独立网页时双击 `网页汉化工作台.exe`，连接 SPT 服务端。Fika 或远程管理需要 Mod 目录 `workbench-admin.jsonc` 里的管理员密钥。API 翻译费用由发起者的供应商账户承担。

更新前备份活动译文、现用术语表和本机配置，替换 DLL 后重启服务端。安装包提供程序和示例，完整译文由用户另行准备。

## 使用与维护

- 安装、手工译文、工作台、日志排错、项目原理和二次开发示例见 [玩家与服主管理手册](docs/PLAYER_GUIDE.zh-CN.md)。
- 术语表格式和保存步骤见 [术语表手册](docs/GLOSSARY_EDITOR.zh-CN.md)；可在浏览器打开 [离线编辑器](tools/glossary-editor/index.html)。
- 接口契约、移植检查和批量汉化流程见 [AI 与开发者接手指南](docs/AI_MAINTAINER_GUIDE.zh-CN.md)。
- 版本数字由维护者决定，详见 [版本号约定](docs/VERSION_POLICY.zh-CN.md)。

源码按职责放在 `src/`，两项目复用的策略在 `Shared/`，文档、测试与打包脚本分别在 `docs/`、`tests/`、`tools/`。维护或发版的完整路径和命令见接手指南。

项目元数据记录作者为 **MiKueYu & Volcano**，许可为 MIT。
