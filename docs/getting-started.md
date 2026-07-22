# 快速开始

## 运行环境与包选择

发布类库只包含 `netstandard2.1` 资产，可由 .NET 5 及后续现代 .NET 应用使用；示例与测试宿主使用 `net10.0`。按需安装包，避免为了 TCP 或 Modbus 引入 OPC UA SDK：

| 需求 | NuGet 包 |
|---|---|
| 公共接口和结果模型 | `Industrial.Communication.Abstractions` |
| 帧、校验、可靠性、监控、历史、回放 | `Industrial.Communication.Core` |
| Serial、TCP、UDP | `Industrial.Communication.Transports` |
| Modbus TCP/RTU | `Industrial.Communication.Protocols.Modbus` |
| Siemens S7 ISO-on-TCP | `Industrial.Communication.Protocols.S7` |
| Mitsubishi MC 3E Binary | `Industrial.Communication.Protocols.Mc` |
| OPC UA | `Industrial.Communication.Protocols.OpcUa` |
| IO、运动、扫码、称重及私有协议适配器 | `Industrial.Communication.Adapters` |
| 可选 DI 注册合集 | `Industrial.Communication.DependencyInjection` |

创建空白项目并安装 TCP/Modbus：

```powershell
dotnet new console -n IndustrialDemo -f net10.0
cd IndustrialDemo
dotnet add package Industrial.Communication.Protocols.Modbus --version 0.1.1
```

TCP 通道和 Modbus 客户端拥有异步生命周期，调用方应使用 `await using`：

```csharp
await using var channel = new TcpTransportChannel(new TcpTransportOptions
{
    Host = "127.0.0.1",
    Port = 502,
    ConnectTimeout = TimeSpan.FromSeconds(3),
});
await using var client = new ModbusClient(channel, ModbusTransportMode.Tcp);

CommunicationResult connected = await client.ConnectAsync(cancellationToken);
if (!connected.IsSuccess)
{
    Console.Error.WriteLine($"{connected.Error!.Code}: {connected.Error.Message}");
    return;
}

CommunicationResult<IReadOnlyList<ushort>> result =
    await client.ReadHoldingRegistersAsync(1, 0, 10, cancellationToken);
```

公共 API 使用零基协议地址。例如 Modbus `HR0` 表示 Holding Register 偏移 0，而不是文档编号 40001。

## 统一 PLC 变量

`IPlcClient` 统一 Modbus、S7、MC 和 OPC UA：

```csharp
var definition = new VariableDefinition(
    "Temperature",
    "HR10",
    PlcDataType.Int16,
    Scale: 0.1,
    Access: VariableAccess.ReadWrite);

CommunicationResult<VariableValue> value = await plc.ReadAsync(definition, cancellationToken);
```

批量操作返回逐变量结果；一个非法地址不会覆盖其他变量的成功结果。`VariableValue` 同时携带 `Good/Uncertain/Bad` 质量、UTC 时间戳及结构化错误。

## 依赖注入

DI 包可以同时注册多个 `IPlcClient`，通过 `IEnumerable<IPlcClient>` 解析：

```csharp
services.AddModbusPlcClient(
    _ => new TcpTransportChannel(new TcpTransportOptions
    {
        Host = "127.0.0.1",
        Port = 502,
    }),
    ModbusTransportMode.Tcp);

services.AddOpcUaPlcClient(_ => new OpcUaConnectionOptions
{
    EndpointUrl = "opc.tcp://localhost:4840",
});
```

默认生命周期是 Singleton。工厂创建的通道和会话由最外层客户端释放，并由 DI 容器跟踪。不要在容器之外再次释放同一个实例。

## 示例

```powershell
dotnet run --project samples/ServerAndSimulator -- 5020
dotnet run --project samples/BasicClient -- 127.0.0.1 5020 "hello"

dotnet run --project samples/ServerAndSimulator -- modbus 5020
dotnet run --project samples/BasicClient -- modbus 127.0.0.1 5020

dotnet run --project samples/PlcVariableMonitor
dotnet run --project samples/OpcUaAndDevices
```

协议地址、能力边界和安全配置分别参阅 `modbus-guide.md`、`plc-variable-guide.md`、`opcua-and-device-adapters.md` 与 `security.md`。
