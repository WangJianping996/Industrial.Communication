# Industrial Communication

面向 .NET Standard 2.1 的通用工业通信类库。目前处于 `0.1.0-preview.1` 开发阶段；现代 .NET 应用可直接引用该 NuGet 包。

七天 MVP 已完成：通用传输与可靠通信、监控/历史/回放、Modbus TCP/RTU、统一 PLC 变量体系、Siemens S7 ISO-on-TCP、Mitsubishi MC 3E Binary、OPC UA、通用设备适配器，以及测试、API 基线、CI/CD 和 NuGet 发布演练。

```powershell
dotnet add package Industrial.Communication.Protocols.Modbus --version 0.1.0-preview.1
```

```powershell
dotnet build Communication.sln -c Release
dotnet test Communication.sln -c Release
dotnet pack Communication.sln -c Release -o artifacts/packages
dotnet run --project tools/ApiSurface -- --verify
./eng/verify-packages.ps1 -PackageDirectory artifacts/packages
```

从 [完整使用指南](docs/user-guide.md) 或 [快速开始](docs/getting-started.md) 入门。进一步参阅 [架构说明](docs/architecture.md)、[Modbus](docs/modbus-guide.md)、[PLC/S7/MC](docs/plc-variable-guide.md)、[OPC UA 与设备适配器](docs/opcua-and-device-adapters.md)、[可靠性与可观测性](docs/reliability-and-observability.md)、[故障排查](docs/troubleshooting.md)、[安全说明](docs/security.md)、[API 兼容性](docs/api-reference.md)、[支持矩阵](docs/supported-features.md) 和 [发布清单](docs/release-checklist.md)。

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
