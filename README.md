# QuickTranslate

QuickTranslate 是一个面向 Windows 10/11 的划词翻译与文字处理工具。选中网页、Word 或其他应用中的文字，按下快捷键，即可在屏幕右上角打开一个始终置顶的助手窗口，完成翻译、解释、总结或自定义任务。

应用通过本机的 Magpie 代理调用已配置的 AI 模型，不内置 API Key，也不会把凭据写入源码。

[![build](https://github.com/goodwei567/quicktranslate/actions/workflows/build.yml/badge.svg)](https://github.com/goodwei567/quicktranslate/actions/workflows/build.yml)

## 功能

- 默认按 `Ctrl+Shift+T` 读取当前选中的文字，也可以自定义快捷键。
- 通过 `Ctrl+C` 获取选区，并尽量恢复剪贴板中的原内容。
- 结果窗口立即显示、始终置顶，默认位于当前屏幕右上角。
- 支持继续追问选区和翻译结果，而不只显示一次性译文。
- 可切换翻译、解释、总结预设，也可以完全自定义提示词。
- 主界面与划词助手共享目标语言和任务提示词。
- 支持修改快捷键和提示词，保存后立即生效。
- 支持关闭到系统托盘、托盘重新打开/设置/退出，以及可选的开机后台启动。
- 单实例运行：再次启动程序会唤回已运行的窗口。

## 系统要求

### 使用发布包

- Windows 10 1809（build 17763）或更高版本，推荐 Windows 10 22H2 / Windows 11。
- x64 处理器。
- 可运行的本机 Magpie 服务，默认 API 地址为 `http://127.0.0.1:3425/v1`，且实现 OpenAI 兼容的 `GET /models` 与 `POST /chat/completions` 接口。

发布包是自包含的免安装版本，不要求另外安装 .NET 运行时或 Windows App SDK Runtime。

### 从源码构建

- .NET 8 SDK。
- Windows 10/11 SDK 或项目引用的 Windows SDK BuildTools NuGet 包。
- Visual Studio 2022 的“使用 C++ 的桌面开发”与 Windows App SDK 组件是可选的；命令行构建更直接。

本项目只支持 unpackaged 桌面运行，不需要开启 Windows 开发者模式。

## 快速开始

1. 启动并配置 Magpie，确认本机 OpenAI 兼容接口可以访问。
2. 下载并解压 `QuickTranslate-v1.0.0-win-x64.zip`。
3. 运行 `QuickTranslate.exe`。
4. 在工作台确认 Magpie 地址和模型，然后保存设置。
5. 在浏览器或 Word 中选中文字，按 `Ctrl+Shift+T`。

首次运行如出现 Windows SmartScreen 提示，是因为发布包未做代码签名。请只从本仓库的 Releases 页面下载，并在核对来源后选择运行。

## 设置说明

配置文件位于：

```text
%LOCALAPPDATA%\QuickTranslate\settings.json
```

“开机启动”和“关闭到托盘”可以在应用的设置页面调整：

- **开机启动**默认关闭。开启并保存后，会在当前用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下写入 `QuickTranslate` 启动项。
- **关闭后缩小到托盘**默认开启。关闭后，点击主窗口的 X 会直接退出。
- 移动或删除程序目录后，需要重新保存开机启动设置。

## 构建

安装 .NET 8 SDK 后，在仓库根目录运行：

```powershell
dotnet restore .\QuickTranslate.csproj -r win-x64
dotnet build .\QuickTranslate.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

生成免安装发布包：

```powershell
.\scripts\publish-win-x64.ps1
```

脚本会将程序和运行依赖写入 `artifacts\publish\win-x64`，并在 `artifacts` 下生成 ZIP。

## 隐私与限制

- QuickTranslate 不直接保存 AI API Key；请求由 Magpie 及其上游模型服务处理。
- 选中文字会发送到你配置的 Magpie 地址。Magpie 上游如何处理数据，取决于你的 Magpie 配置与模型供应商条款。
- 选区读取依赖 `Ctrl+C`。如果目标应用禁止复制、选区位于受保护区域，或文字只存在于图片中，应用无法提取文字。
- 当前版本不含 OCR、普通离线翻译、翻译历史或云同步。
- 剪贴板恢复是尽力而为；复杂格式、图片和文件型剪贴板内容可能无法完全原样恢复。

## 故障排查

### 提示无法连接 Magpie

确认 Magpie 正在运行，并在工作台刷新模型。默认地址是：

```text
http://127.0.0.1:3425/v1
```

### 按快捷键后没有选中文字

先确认目标文字可以被 `Ctrl+C` 复制。PDF、图片、受保护文档或远程桌面窗口可能需要使用 OCR 或专门工具。

### 快捷键没有反应或被占用

在设置页面选择其他组合并保存。保存失败时，应用会保留原先可用的快捷键。

### 关闭窗口后找不到程序

单击 Windows 系统托盘中的 QuickTranslate 图标可以重新打开主窗口。右键该图标可选择“设置”或“退出”。

## 许可

本项目使用 [MIT License](LICENSE)。
