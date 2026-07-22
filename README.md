# Industrial Communication

面向 .NET Standard 2.1 的通用工业通信类库，可用于现代 .NET 应用中的设备连接、协议通信、数据采集和状态监控。

## 安装

```powershell
dotnet add package Industrial.Communication.Protocols.Modbus --version 0.1.1
```

按需安装对应组件包：

- `Industrial.Communication.Abstractions`：公共契约与数据模型
- `Industrial.Communication.Core`：可靠通信、重试、队列、监控、历史与回放
- `Industrial.Communication.Transports`：Serial、TCP Client/Server 和 UDP 传输
- `Industrial.Communication.Protocols.Modbus`：Modbus TCP/RTU
- `Industrial.Communication.Protocols.S7`：Siemens S7 ISO-on-TCP
- `Industrial.Communication.Protocols.Mc`：Mitsubishi MC 3E Binary
- `Industrial.Communication.Protocols.OpcUa`：OPC UA
- `Industrial.Communication.Adapters`：数字 IO、运动控制、扫码和称重设备适配器
- `Industrial.Communication.DependencyInjection`：依赖注入注册扩展

## 文档与示例

从 [完整使用指南][user-guide] 或 [快速开始][getting-started] 入门。进一步参阅 [架构说明][architecture]、[Modbus][modbus]、[PLC/S7/MC][plc]、[OPC UA 与设备适配器][opcua]、[可靠性与可观测性][reliability]、[故障排查][troubleshooting]、[安全说明][security]、[API 兼容性][api] 和 [支持矩阵][features]。

[user-guide]: https://github.com/WangJianping996/Industrial.Communication/blob/main/docs/user-guide.md
[getting-started]: https://github.com/WangJianping996/Industrial.Communication/blob/main/docs/getting-started.md
[architecture]: https://github.com/WangJianping996/Industrial.Communication/blob/main/docs/architecture.md
[modbus]: https://github.com/WangJianping996/Industrial.Communication/blob/main/docs/modbus-guide.md
[plc]: https://github.com/WangJianping996/Industrial.Communication/blob/main/docs/plc-variable-guide.md
[opcua]: https://github.com/WangJianping996/Industrial.Communication/blob/main/docs/opcua-and-device-adapters.md
[reliability]: https://github.com/WangJianping996/Industrial.Communication/blob/main/docs/reliability-and-observability.md
[troubleshooting]: https://github.com/WangJianping996/Industrial.Communication/blob/main/docs/troubleshooting.md
[security]: https://github.com/WangJianping996/Industrial.Communication/blob/main/docs/security.md
[api]: https://github.com/WangJianping996/Industrial.Communication/blob/main/docs/api-reference.md
[features]: https://github.com/WangJianping996/Industrial.Communication/blob/main/docs/supported-features.md

运行 TCP Echo 示例：

```powershell
dotnet run --project samples/ServerAndSimulator -- 5020
dotnet run --project samples/BasicClient -- 127.0.0.1 5020 "hello"
```

运行 Modbus TCP 模拟器与客户端：

```powershell
dotnet run --project samples/ServerAndSimulator -- modbus 5020
dotnet run --project samples/BasicClient -- modbus 127.0.0.1 5020
```

运行 Modbus、S7、MC 统一变量监视示例：

```powershell
dotnet run --project samples/PlcVariableMonitor
```

运行 OPC UA 订阅恢复与数字 IO 适配器示例：

```powershell
dotnet run --project samples/OpcUaAndDevices
```
