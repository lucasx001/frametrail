# Windows 原生工具链

冻结日期：2026-10-14。目标是 Windows 11 x64，本文件区分工程配置与实际验收。

## 平台与版本

| 项目 | 固定值 / 约束 |
|---|---|
| 运行系统最低 API build | Windows 11 22H2，build 22621；实际产品支持仍要求微软支持周期内的 Windows 11 |
| CPU / ABI | x64、cdecl、C++20、静态 MSVC CRT（/MT，Debug 为 /MTd） |
| .NET SDK | 8.0.426，global.json 仅允许同 feature band 的稳定 patch 前滚 |
| App target | net8.0-windows10.0.26100.0，TargetPlatformMinVersion=10.0.22621.0 |
| Domain / Interop | net8.0，x64 |
| Windows App SDK | NuGet Microsoft.WindowsAppSDK 1.8.260921001（1.8.12 stable） |
| SDK BuildTools NuGet | Microsoft.Windows.SDK.BuildTools 10.0.26100.4948 |
| Native SDK | Windows SDK 10.0.26100.0 |
| Native compiler | MSVC v143；本机验证版本14.44.35207（cl19.44） |
| CMake | 最低3.25；本机工具版本3.31.6 |
| Ninja | 本机工具版本1.11.1（Python distribution1.11.1.4） |
| CI | GitHub hosted windows-2022 / VS2022；实际云端执行结果另记 |

Windows App SDK 1.8 本身支持更早系统；本项目有意缩小为 Windows 11。所有 26100 专有能力都必须运行时检测或在此提高最低 build，不能仅依靠编译通过。任务0003会记录设备与采集 API 能力。

.NET8于2026年11月结束支持，因此当前版本是M0/M1的固定验证基线；在正式发行前必须安排升级到受支持LTS（优先.NET10），重新执行WinUI、原生桥接和打包验收。

## 开发环境

标准环境：Visual Studio 2022 Build Tools，包含 C++ desktop workload、MSVC v143、Windows11 SDK26100与CMake；另安装.NET SDK8.0.426。WinUI C#项目可由dotnet build调用包内XAML编译器，不依赖IDE模板。

本机也支持用户目录 portable 工具链。脚本默认探测：

```text
%USERPROFILE%\AppData\Local\FrameTrail\Toolchain\
  dotnet\dotnet.exe
  msvc\setup_x64.bat
  python-packages\cmake\data\bin\cmake.exe
  python-packages\ninja\data\bin\ninja.exe
  python-packages\bin\ninja.exe    # 部分 pip wheel 的实际布局
```

使用`-ToolchainRoot`可覆盖；脚本只把该工具链导入当前PowerShell进程，不修改系统PATH。Portable MSVC必须来自微软签名的官方组件，下载工具本身不算项目依赖。

## 从干净checkout构建

在项目根目录打开PowerShell：

```powershell
.\scripts\doctor.cmd
.\scripts\build.cmd -Configuration Debug
.\scripts\test.cmd -Configuration Debug
.\scripts\test.cmd -Configuration Debug -IncludeHardware  # 登录桌面的设备测试
.\scripts\run.cmd
```

只有Visual Studio生成器时：

```powershell
.\scripts\build.cmd -Configuration Release -Generator "Visual Studio 17 2022"
```

已初始化cl环境或使用portable工具链时默认Ninja。不同生成器或不同Configuration用不同`-NativeBuildDirectory`，避免复用CMake缓存。可以传入`-NativeOnly`只构建原生引擎。已提交packages.lock.json后，CI使用`-LockedRestore`拒绝未审阅的依赖解析变化。

### PowerShell 禁止运行脚本

如果直接执行 `.ps1` 出现 `PSSecurityException` / `UnauthorizedAccess`，脚本尚未开始运行。Windows PowerShell 在未配置执行策略时通常使用 `Restricted`；PowerShell 7 的设置可能不同。

优先使用上面的 `.cmd` 入口。它们通过系统自带的 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File` 执行同名脚本，保留脚本参数与退出码，且不修改用户或系统的持久执行策略。该方式不需要管理员权限；企业组策略仍具有更高优先级。

也可以只对单次调用显式指定策略：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Configuration Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\run.ps1 -Configuration Release
```

可在报错的终端中运行 `Get-ExecutionPolicy -List` 检查各作用域。策略作用域与优先级见 [Microsoft 执行策略说明](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_execution_policies?view=powershell-5.1)。

WinUI应用采用unpackaged + WindowsAppSDKSelfContained，构建时复制FrameTrail.Engine/Capture/Audio/Recording/Media DLL到应用目录。原生模块静态链接 CRT，避免缺少 Debug CRT 或加载过旧宿主运行库；所有跨 ABI 的缓冲由调用方提供，对象由创建它的 DLL 销毁，不跨 CRT 释放内存。媒体像素留在原生引擎，C#仅持有句柄及状态。当前是开发目录启动；MSIX签名、安装更新和发布留到0011。

## ABI与生命周期

- ABI1，`engine.h`为公开协议；UTF-8、显式uint32字节容量，字符串容量包含结尾NUL。
- Session是单调递增uint64句柄，0无效。引擎注册表拒绝过期句柄与重复销毁；不将已释放指针重新解释为对象。
- C# `EngineSafeHandle`拥有句柄；P/Invoke通过SafeHandle固定调用期间生命周期；Dispose可重复调用。
- 所有入口同步、线程安全、无托管回调；采集线程不允许直接触碰XAML。
- 错误保存在调用OS线程，失败后立即读取；C++异常被转换为稳定错误码。
- 取消是幂等请求；基础Session仅维护取消状态，具体媒体任务负责观察取消信号。

## 测试与许可证

原生测试使用项目内C++ executable + CTest，无第三方框架。托管桥接测试采用Microsoft.NET.Test.Sdk17.14.1（MIT）、xUnit2.9.3（Apache2.0）、xunit.runner.visualstudio3.1.1（Apache2.0）。所有第三方二进制仍以其包内许可证为准。领域层JSON采用nlohmann/json3.11.3（MIT），详见该模块依赖配置。

CPU测试验证ABI、取消、1000次生命周期、重复销毁与并发。WinUI窗口、WGC采集、真实音频设备、摄像头和GPU效果必须在登录的Windows桌面上单独验收；CI成功不能替代这些证据。

托管设备测试使用 `Category=Hardware`，默认脚本和 hosted CI 排除，实机通过 `-IncludeHardware` 明确执行。TRX 使用各测试项目独立文件名，避免覆盖。CI 的 `-LockedRestore` 同时约束 App 和测试依赖；实际云端 CI 尚未执行。

## 技术依据

- [Windows App SDK1.8 release notes](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-1-8)：1.8.12稳定版于2026-09-24发布。
- [NuGet WindowsAppSDK](https://www.nuget.org/packages/Microsoft.WindowsAppSDK/1.8.260921001)：固定包版本。
- [.NET8下载](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)与[支持政策](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)。
- [WinUI3 unpackaged部署](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-unpackaged-apps)。
- [Per-Monitor DPI awareness](https://learn.microsoft.com/en-us/windows/win32/hidpi/setting-the-default-dpi-awareness-for-a-process)。

具体构建、测试与UI运行结果见`docs/validation/0001-*.md`，未执行检查不视为完成。

## M2 媒体验证

`validate-m2.cmd -Matrix` 在全新 artifacts 子目录生成合成素材，运行原生解码/合成、剪辑、MP4/M4A/素材副本及六组输出能力验证，再用独立 Python 脚本核查图像、烧录帧号、音调和样本数。额外使用本机 ffmpeg、ffprobe 与 Python 标准库，只是开发验证依赖；产品不调用 FFmpeg。结果见 [M2 记录](validation/m2-editor-export.md)。

`run.cmd -Configuration Release -ProjectPath <工程目录或project.json>` 可直接打开编辑器。普通运行入口会给当前进程设置项目 portable .NET 的 DOTNET_ROOT，不修改系统环境。
