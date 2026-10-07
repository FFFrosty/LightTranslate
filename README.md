# 轻译 LightTranslate

轻译是一个独立的 Windows 托盘翻译工具：在网页、文档或其他桌面程序中选中文字，点击选区旁的「译」按钮即可调用 AI 翻译。

项目地址：<https://github.com/FFFrosty/LightTranslate>

## 功能

- 选中文字后显示浮动的「译」按钮，点击后打开可复制的翻译结果。
- 提供 `Ctrl + Alt + T` 快捷键和托盘菜单中的手动翻译入口。
- 选择文字时不调用网络服务；只有点击翻译、使用快捷键或提交手动翻译后才发送文字。
- 根据导入配置中的目标语言和备用语言自动决定翻译方向。默认配置为外文译为简体中文、中文译为英文。
- API Key 使用 Windows 当前用户的 DPAPI 加密，保存在 `%LOCALAPPDATA%\LightTranslate\profile.bin`；程序不保存翻译历史。

## 配置方式

当前版本没有手工填写 API 地址、模型和 API Key 的设置界面。首次使用需要先在 Cherry Studio 中配置可用的翻译模型，再由轻译导入配置。

1. 确认 Cherry Studio 已配置启用的模型、服务和 API Key。
2. 启动 `LightTranslate.exe`。程序会读取 Cherry Studio 的 SQLite 配置；默认位置是 `%APPDATA%\CherryStudio\Data\cherrystudio.sqlite`。
3. 如果需要重新选择模型，在托盘菜单中选择「重新导入 Cherry 配置」；也可以使用「使用 Cherry 中的 DeepSeek 配置」。

轻译只读取选中的翻译模型、对应的服务、语言偏好和启用的 API Key。导入后会保存一份独立的加密配置，之后翻译不需要 Cherry Studio 保持运行。重新导入时才需要再次读取 Cherry Studio 数据库。

目前支持 Cherry Studio 中的 OpenAI 兼容 Chat Completions 配置，并要求服务地址使用 HTTPS（本机 localhost 可使用 HTTP）。其他旧版配置或接口协议可能无法导入。

## 使用

1. 双击 `LightTranslate.exe`，程序会常驻系统托盘。
2. 在目标程序中拖动鼠标选中文字。
3. 点击选区附近的「译」按钮。
4. 如果目标程序不提供可访问的选区，可以在选中文字后按 `Ctrl + Alt + T`；也可以从托盘打开「手动翻译」窗口并粘贴文字。

图片、扫描件和密码框不在当前版本支持范围内。管理员权限程序、部分 PDF 阅读器、远程桌面和自绘控件可能无法读取选区；这种情况下可以使用手动翻译入口。单次输入最多 12,000 个字符。

## 隐私与安全

程序不会在选中文字时发送网络请求，也不会保存翻译历史。点击翻译后，选中的文字会发送到导入配置指定的服务地址；服务方的留存和隐私政策由对应服务决定。API Key 只在内存中使用，并以当前 Windows 用户可解密的方式保存到独立配置文件中。

## 运行环境

- Windows 10 或 Windows 11
- .NET 8 Windows Desktop Runtime
- C# / .NET 8
- 无第三方 NuGet 依赖

发布包需要目标电脑已安装 .NET 8 Windows Desktop Runtime。程序不会自动设置开机启动。

## 构建

在仓库根目录执行：

```powershell
.\build.ps1
```

脚本会先运行核心测试，再把程序发布到 `artifacts\LightTranslate`，并复制本说明文件。也可以指定输出目录：

```powershell
.\build.ps1 -Output .\artifacts\LightTranslate
```

构建需要 .NET 8 SDK。`tests\CoreTests` 是离线核心测试；`tests\DesktopTests` 是 Windows 桌面集成测试，默认不会被 CI 自动运行，因为它需要真实桌面、鼠标和窗口焦点。

## 桌面测试

只验证离线测试控件时执行：

```powershell
dotnet run --project tests\DesktopTests\CherryTranslate.DesktopTests.csproj -- `
  --fixture --screenshot .artifacts\fixture.png
```

使用真实发布程序进行端到端演示时执行：

```powershell
dotnet run --project tests\DesktopTests\CherryTranslate.DesktopTests.csproj -- `
  --app .\artifacts\LightTranslate\LightTranslate.exe `
  --app-arg --demo `
  --app-arg --selftest `
  --app-arg --exit-after=15 `
  --button-title '译' `
  --timeout-ms 5000 `
  --startup-ms 1500 `
  --screenshot .artifacts\app.png
```

`--fixture` 只检查离线控件选择、浮动按钮和演示结果，不启动生产程序。`--app` 流程会启动真实程序，在独立测试窗口中进行物理鼠标选取，可能移动鼠标、激活测试窗口并在取词回退路径中短暂使用剪贴板；请在空闲桌面运行。轻译的取词服务会尽力恢复原剪贴板内容，测试进程只会关闭由它启动的程序。

## 许可

本项目以 MIT License 发布，详见 [LICENSE](LICENSE)。
