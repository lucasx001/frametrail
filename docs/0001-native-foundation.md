# 0001 — Windows 原生工程基础

状态：已完成。依赖：无。规格依据：[spec.md](spec.md)。进度入口：[progress.md](progress.md)。

## Task 整体描述

建立可重复构建的 WinUI 3 应用、C++ 原生引擎和测试入口，验证 UI 与引擎的最小调用闭环。此任务不安装未确认的大型开发工具，不把环境存在误记为应用可运行。

## Task 具体实现逻辑

1. 检查 Windows build、Visual Studio Build Tools、Windows SDK、.NET SDK、CMake 和 WinUI 模板。缺少工具时记录明确的安装项与阻塞原因。
2. 核对稳定版兼容矩阵，固定 SDK/工具链版本、最低系统 build；记录到 docs/toolchain.md，提供从干净 checkout 构建的说明。
3. 建立 src/FrameTrail.App、src/FrameTrail.Interop、native/FrameTrail.Engine、tests/FrameTrail.Core.Tests、tests/FrameTrail.App.Tests 和 scripts。
4. 使用 MSBuild 构建 WinUI 项目，CMake 构建 C++20 引擎；统一 x64、配置和 DLL 输出目录，避免开发机 PATH 隐式依赖。
5. C ABI 提供版本查询、创建/销毁 Session、取消和错误读取。约定 UTF-8、字符串长度、结构体布局、句柄所有权和回调线程；C# 用 SafeHandle 包装，禁止 native 异常跨 ABI。
6. 打开原生主窗口并显示引擎版本；加入正常关闭和重复创建/销毁的验证。测试框架选择稳定版本并记录许可证。
7. 建立 Windows CI 的编译与纯逻辑测试入口；涉及 GPU/交互桌面的测试明确留到专用机器。

## 验收 Todo

- [x] 工具链版本、最低 build、依赖版本和构建命令已记录
- [x] 干净构建产出 WinUI 应用及原生 DLL
- [x] 窗口能够启动、显示真实引擎版本并正常关闭
- [x] ABI 错误返回、取消及重复释放防护通过测试
- [x] CI 可运行无 GPU 的测试，并明确实机测试边界

## 交付物

可构建工程、版本化桥接协议、工具链文档和基础 CI。

## 验收记录

- 实现日期：2026-10-14；本次未创建提交。
- 构建与自动测试：已执行；主 agent 集成构建、测试及具体证据见下方记录。
- 实机环境与结果：Windows 11 x64 build 26300；已测与未测范围见验证记录。
- 证据路径：[验证记录](validation/0001-foundation.md)。
- 已知限制：基线 main 的云端 CI 已核查成功（见本轮验证）；本轮分支的云端 CI 与本地验证分别记录，Mac 程序对照未执行。

完成后更新本文件清单与 progress.md；需求变化同时修改 spec.md。未达到的验收项保持未勾选。
