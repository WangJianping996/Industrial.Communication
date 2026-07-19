# 第三方依赖与许可证决策

评审日期：2026-07-19。版本与许可证在每次发布前必须重新核对，并使用锁定文件或中央版本管理固定。

## 决策原则

- `Communication.Abstractions` 不依赖任何 PLC SDK、Socket 封装库或日志实现。
- 厂商/协议依赖只能存在于对应的独立协议包，且其类型不得出现在公共 API。
- 优先选择允许商业使用、维护活跃、兼容 `netstandard2.1` 且依赖最小的库。
- LGPL、GPL、双许可证或非 SPDX 自定义协议需要发布方完成法律复核，未经确认不引入。

## 候选评审

| 领域 | 候选 | 截止评审日版本 | 许可证/目标框架 | 决策 |
|---|---|---:|---|---|
| Modbus | [NModbus](https://github.com/NModbus/NModbus) | 3.0.83 | MIT；NuGet 提供 .NET Standard 1.3 资产 | 不作为运行时依赖。当前实现按公开规范提供冻结范围的 codec、客户端和模拟器，Golden Tests 直接依据规范字节。 |
| Siemens S7 | [S7netplus](https://github.com/S7NetPlus/s7netplus) | 0.20.0 | MIT；其 .NET Standard 2.0 资产可被 .NET Standard 2.1 项目引用 | 不作为首版运行时依赖。当前实现提供限定的 ISO-on-TCP/COTP/S7comm 绝对字节访问；`IS7DataAccess` 保留未来替换边界。 |
| Mitsubishi MC | 无合适的最小 C# 依赖 | — | 部分通用工业库许可证或依赖面不符合核心包目标 | 当前实现按公开 MC 3E Binary 规范提供限定命令；`IMcDataAccess` 保留未来替换边界。 |
| OPC UA | [OPC Foundation .NET Standard SDK](https://github.com/OPCFoundation/UA-.NETStandard) | 1.5.378.156 | OPC Foundation MIT License；客户端 NuGet 提供 `netstandard2.1` 资产 | 已引入独立 `Industrial.Communication.Protocols.OpcUa` 包；SDK 类型由 `OpcFoundationSession` 隔离，不进入公共通用接口。默认拒绝未信任证书。 |

## 基础兼容依赖

.NET Standard 2.1 已包含 `IAsyncEnumerable<T>`、`IAsyncDisposable`、`ReadOnlyMemory<T>`、`ReadOnlySpan<T>` 和 `ReadOnlySequence<T>` 所需契约，因此 `Communication.Abstractions` 不再需要 `Microsoft.Bcl.AsyncInterfaces` 或 `System.Memory` 兼容包。

基础通信实现使用以下依赖：

- `System.Threading.Channels` 10.0.9（MIT）：仅由 Core/Transports 使用，为有界异步队列和 TCP Server 请求队列提供基础设施；
- `System.IO.Ports` 10.0.10（MIT）：仅由 Transports 使用，提供跨平台 `SerialPort` 及其 `BaseStream`。

两者均不进入 `Communication.Abstractions` 的依赖图。

`System.Text.Json` 10.0.10（MIT）仅由 Core 用于 JSON Lines 历史和 JSON 导出。该包提供 .NET Standard 2.0 资产，可由 `netstandard2.1` 类库引用；序列化类型保持为 Core 内部实现，不进入公共契约。

## NuGet 包拆分

发布包：

- `Industrial.Communication.Abstractions`
- `Industrial.Communication.Core`
- `Industrial.Communication.Transports`
- `Industrial.Communication.Protocols.Modbus`
- `Industrial.Communication.Protocols.S7`
- `Industrial.Communication.Protocols.Mc`
- `Industrial.Communication.Protocols.OpcUa`
- `Industrial.Communication.Adapters`
- `Industrial.Communication.DependencyInjection`

上述包已由 `WJP` 账户发布到 NuGet.org，并通过 Trusted Publishing 完成自动化发布。
