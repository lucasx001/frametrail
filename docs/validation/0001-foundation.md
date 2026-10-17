# 0001 工程基础验证记录

> 历史执行快照。后续集成/修复见 [m0-m1-review.md](m0-m1-review.md)；当前任务状态以 [progress.md](../progress.md) 和 [待办](../todo.md) 为准。本页未勾选项仅反映记录当日范围。


日期：2026-10-14。执行环境：Windows x64，OS API version10.0.26300；用户当前登录会话。此记录只陈述已执行项，主agent会继续补充集成验收。

## 已完成的实现

- WinUI3/C#/.NET8 unpackaged应用：PerMonitorV2 manifest、真实引擎版本显示、创建/取消/释放按钮、关闭释放Session。
- C ABI1：版本查询、uint64 Session注册表、稳定错误码、UTF-8错误读取、线程安全取消、无异常跨ABI。
- P/Invoke + SafeHandle：会话生命周期、取消和Dispose检查；像素不经过托管桥接。
- CMake入口、C++测试、xUnit桥接测试、Windows CI、PowerShell build/test/run/doctor。
- portable MSVC/.NET/CMake/Ninja工具链探测，不写系统PATH。
- `--smoke-test <absolute-result.json>`：窗口实际Loaded后调用真实引擎，循环100次会话/取消，写JSON并正常关闭；尚未执行，不能视作通过。

## 真实执行

使用父agent准备的用户目录工具链：MSVC14.44.35207、Windows SDK26100。

由于.NET SDK与CMake仍在下载，先直接用cl编译独立基础DLL和测试：

```powershell
. .\scripts\common.ps1
Import-FrameTrailToolchain ''
# cl /std:c++20 /EHsc /MD /W4 /utf-8，原生DLL + 测试executable
.\artifacts\foundation-smoke\FrameTrail.Engine.Tests.exe
```

结果：exit0，输出`PASS: engine ABI, buffers, cancellation, 1000 lifecycles, concurrency`。

覆盖：版本与字符串长度查询、小缓冲区、ABI不匹配、null参数、错误读取、1000次创建/取消/销毁、重复销毁拒绝、过期句柄拒绝、8线程并发取消。

初次编译产生strncpy弃用警告，已改为显式长度memcpy；再次编译最终源码无该警告，测试再次exit0。全部PowerShell脚本Parser语法检查通过。

## 尚待主agent执行

- [ ] CMake Debug/Release完整构建（领域与采集模块由其他agent并行实现）。
- [ ] dotnet restore生成并提交packages.lock.json；锁定还原检查。
- [ ] WinUI XAML完整构建与原生DLL输出复制。
- [ ] xUnit桥接测试实际运行。
- [ ] 真实启动WinUI窗口，smoke JSON success=true并正常退出。
- [ ] 人工视觉、键盘交互与高DPI检查。
- [ ] GitHub Actions云端运行；目前只创建workflow，不宣称执行成功。

## 集成注意

1. SDK8.0.426 global.json已固定；脚本优先portable dotnet，避免系统只有runtime的dotnet抢先。
2. portable根默认`%USERPROFILE%\AppData\Local\FrameTrail\Toolchain`，可传`-ToolchainRoot`。
3. CI使用`-LockedRestore`；生成并提交锁文件前不能宣称CI可通过。
4. App依赖WinUI1.8.12稳定包1.8.260921001、SDK BuildTools10.0.26100.4948，NuGet版本存在性已查询确认。XAML实际编译尚待验证。
