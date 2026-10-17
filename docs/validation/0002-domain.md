# 0002 领域模型验证记录

> 历史执行快照。后续集成/修复见 [m0-m1-review.md](m0-m1-review.md)；当前任务状态以 [progress.md](../progress.md) 和 [待办](../todo.md) 为准。本页未勾选项仅反映记录当日范围。


日期：2026-10-14。系统：本次 Windows x64 工作区。工程路径：`C:\Users\lucas\Desktop\project\frametrail`。

## 已执行

原生工具链由主 agent 部署于 `%LOCALAPPDATA%\FrameTrail\Toolchain`；使用 MSVC 14.44、CMake/Ninja、C++20。托管使用 .NET SDK 8.0.426、net8.0、xUnit 2.9.3 和 Microsoft.NET.Test.Sdk 17.14.1。JSON 依赖为 nlohmann/json 3.11.3；真实下载后锁定归档 SHA256 `0d8ef5af7f9794e3263480193c491549b2ba6cc74bb018906202ada498a79406`。

```powershell
. ./scripts/common.ps1
Import-FrameTrailToolchain
cmake -S . -B artifacts/native -G Ninja -DCMAKE_BUILD_TYPE=Debug -DBUILD_TESTING=ON
cmake --build artifacts/native --target FrameTrail.Core.Tests FrameTrail.Engine --parallel 2
./artifacts/native/bin/FrameTrail.Core.Tests.exe
dotnet test tests/FrameTrail.Domain.Tests/FrameTrail.Domain.Tests.csproj --verbosity minimal --logger 'trx;LogFileName=0002-domain.trx' --results-directory artifacts/test-results
```

- C++：25370 检查通过，覆盖人工映射向量、裁剪删除、重排、0.25/1/4 倍速及 1.5 倍有理速率、切口端点、反向映射容差、7 天极限、取整与溢出、光标分组与 seed、基础场景和非法 JSON。
- .NET：62 个测试通过、0 跳过。其中 55 个协议/序列化测试，7 个真实 C ABI 集成测试。
- C ABI 验证实际 DLL 装载、C# 生成 JSON 的原生解析、同一映射向量的两个方向、FrameScene 结构布局、辅助轨偏移、场景缺席、取消/释放、原生语义拒绝、过期/并发修订保护。
- 两层共同消费 `project-v1.json` 及 33 项 `protocol-invalid-cases.json`；Core 和 C# 一致拒绝这些协议负例。
- 证据：`artifacts/test-results/0002-domain.trx`（生成文件），`tests/FrameTrail.Core.Tests` 的可重复执行代码和 fixtures。

## 修复记录

最初跨 ABI 运行发生 `ft_session_create` AccessViolation，原生 Engine.Tests 同样崩溃。此时 Core 纯算法与 DTO 测试通过，但不能作为 ABI 通过。主 agent 把整个工程 MSVC CRT 切为静态链接，重新构建后上述 62 个测试通过；没有把 Debug CRT 当发布依赖复制进产品。

## 尚未执行 / 范围边界

- 没有在 Mac 上运行 Swift 版本，同一算法的双平台程序输出对照未完成；当前向量为人工推导并参照 Swift 源码。
- 不验证录屏、硬件编码、音画同步、GPU 渲染或编辑器交互；这些属于后续任务。
- FrameScene 当前只求基础 screen/background/camera，效果参数仅保存与校验。
- 相对路径校验为词法层面；磁盘存在性与 reparse point 检查由持久化层负责。

主 agent review 和最终集成验证后，统一修改 `progress.md` 及 task 文档验收清单。
