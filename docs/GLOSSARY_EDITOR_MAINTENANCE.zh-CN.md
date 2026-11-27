# 术语编辑器维护说明

维护者决定版本号，详见 [VERSION_POLICY.zh-CN.md](VERSION_POLICY.zh-CN.md)。

## 代码边界

- 两项目 `Shared/TranslationGlossary.cs` 是任务使用的不可变执行快照，`Shared/GlossaryDocument.cs` 是可编辑文件模型。解析、保存校验及预览复用执行解析器；修改公共代码后同步两项目并更新 `Shared/manifest.json` 的 SHA256。
- 客户端 `GlossaryEditorSession.cs` 管文件读取、基线冲突检测、原子替换和备份；`GlossaryEditorWindow.cs` 只管理草稿与 IMGUI。结构变化在 Layout 阶段应用，避免 Mono / IL2CPP 控件数量失配。两 csproj 都须包含新源文件。窗口使用现有 Theme；不新增游戏实体 / IL2CPP 注入类型。
- `tools/glossary-editor/index.html` 与相邻 JS 是离线工具，经典脚本可由 file:// 加载，不依赖 CDN、框架或服务器接口。JS 模型函数也能在 Node 中测试。共享页面脚本的维护源为客户端 tools/glossary-editor。服务端保留经过同步和验证的副本，以单独构建发布包。
- 术语编辑不发起翻译、不保存凭据、不增设服务端写接口。文件系统权限仍由运营者控制；玩家网络投稿须沿用现有管理员审核流程。
- 浏览器预览模拟当前匹配规则，执行端 C# 是最终依据；特别是罕见 Unicode 大小写转换可能因 JS 与 .NET 不同而更严格。请为新增匹配规则补两端用例。浏览器不能保证备份下载完成，UI 与手册必须继续区分下载和保存。

## 最小验证

以下命令在源码仓库运行，安装 ZIP 不含 tests。浏览器测试可使用已安装 Chrome，或 Playwright 的 Chromium；`GLOSSARY_BROWSER_CHANNEL` 可指定其他支持的通道。

在 SimpleTranslatorCS 源码目录：

```powershell
./tools/Verify-Foundation.ps1 -OtherProject ../ConfigurationManager.zh-cn
node tests/glossary-editor.test.cjs
node tests/glossary-editor.browser.cjs
# browser 测试需要 Playwright + Chromium；可通过 NODE_PATH 指定依赖目录。
dotnet test tests/SimpleTranslatorCS.Tests/SimpleTranslatorCS.Tests.csproj -c Release -p:SptTarget=4.0 -p:NuGetAudit=false
dotnet test tests/SimpleTranslatorCS.Tests/SimpleTranslatorCS.Tests.csproj -c Release -p:SptTarget=4.1 -p:NuGetAudit=false
dotnet test tests/SimpleTranslatorCS.Tests/SimpleTranslatorCS.Tests.csproj -c Release -p:SptTarget=5.0be -p:NuGetAudit=false
```

在客户端目录构建两个 csproj 的 Release，运行 `dotnet run --project tests/RegressionTests.csproj` 和 `dotnet run --project tests/Il2CppApiSmoke/Il2CppApiSmoke.csproj`。Mono 另用 `-p:MonoGamePath=...` 验证4.0引用；5.0be需要本机运行时 DLL。服务端三个目标顺序测试，避免共用测试中间输出互相覆盖。

回归重点：停用词条注释兼容、空表不恢复默认、无效格式不写文件 / 不付费、备份保留原字节、外部修改不覆盖、预览按预算筛选、含特殊字符的原文不执行正则 / HTML。浏览器测试直接打开本地 HTML，检查 CSP、交互和下载。还须游戏内验收弹窗焦点、F12隐藏后草稿、关闭确认、字体和较低分辨率；编译和纯逻辑测试不能替代实机画面。

## 发布

完成构建测试后，从 SimpleTranslatorCS 运行 `python tools/package_release.py --server-only --output releases/日期-server`。三个服务端包均在根目录放 `README.zh-CN.md`、`docs/` 操作手册和离线工具；显式文件清单阻止活动术语表、备份、缓存、凭据和调试符号进入包。检查 SHA256 和 ZIP 内容后更新 `releases/LATEST_RELEASE.txt`。不要直接打包个人运行目录。

## 历史验证（2026-10-10）

服务端4.0、4.1、5.0be各125项通过；客户端314个回归断言通过；Mono4.0/4.1与IL2CPP构建均无警告错误。离线模型26项、Chrome交互15项，含外部修改冲突、备份、HTML文本安全与零外部网络请求；工作台布局7项与210个屏幕边界用例、IL2CPP模拟API/模型/术语请求通过。测试不调用真实付费接口。本轮未替换游戏目录中的DLL；实际游戏弹窗和Fika多客户端仍需安装后实机验收。
