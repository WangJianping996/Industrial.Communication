# Modbus TCP/RTU 快速入门

`Industrial.Communication.Protocols.Modbus` 仅提供 `netstandard2.1` 资产。实现依据 Modbus Organization 的 [Application Protocol V1.1b3](https://modbus.org/docs/Modbus_Application_Protocol_V1_1b3.pdf) 和 [Serial Line V1.02](https://modbus.org/docs/Modbus_over_serial_line_V1_02.pdf)，TCP 与 RTU 共用请求、响应、功能码和数据区模型，只替换 ADU 封装。

## 地址规则

所有客户端 API 都接收 **0 基协议地址**。设备手册中的 `40001`、`30001`、`00001` 等是人类展示编号，不直接放入报文：

| 手册表示 | 数据区 | 本库协议地址 |
|---|---|---:|
| `00001` | Coil | `0` |
| `10001` | Discrete Input | `0` |
| `30001` | Input Register | `0` |
| `40001` | Holding Register | `0` |

不同厂商手册可能已经使用 0 基偏移，接入前必须确认，类库不会猜测或自动减一。

## 支持范围

| 功能码 | API | 数量上限 |
|---:|---|---:|
| 01 | `ReadCoilsAsync` | 2000 bits |
| 02 | `ReadDiscreteInputsAsync` | 2000 bits |
| 03 | `ReadHoldingRegistersAsync` | 125 registers |
| 04 | `ReadInputRegistersAsync` | 125 registers |
| 05 | `WriteSingleCoilAsync` | 1 coil |
| 06 | `WriteSingleRegisterAsync` | 1 register |
| 15 | `WriteMultipleCoilsAsync` | 1968 coils |
| 16 | `WriteMultipleRegistersAsync` | 123 registers |

多字节字段采用 Modbus 大端顺序；线圈按每字节最低有效位优先打包。异常响应转换为 `CommunicationErrorCode.DeviceError`，原始异常码保存在 `CommunicationError.Detail`。

## TCP 客户端

```csharp
var transport = new TcpTransportChannel(new TcpTransportOptions
{
    Host = "192.168.1.20",
    Port = 502
});

await using var client = new ModbusClient(
    transport,
    ModbusTransportMode.Tcp,
    retryPolicy: new ExponentialBackoffRetryPolicy(maxAttempts: 3));

await client.ConnectAsync();
var read = await client.ReadHoldingRegistersAsync(
    unitId: 1,
    address: 0,
    quantity: 10);
```

TCP 客户端自动分配事务 ID，允许配置 `MaxTcpInFlight` 后并发请求和乱序响应关联。

## RTU 客户端

```csharp
var serial = new SerialTransportChannel(new SerialTransportOptions
{
    PortName = "COM3",
    BaudRate = 9600,
    DataBits = 8,
    Parity = Parity.Even,
    StopBits = StopBits.One
});

await using var client = new ModbusClient(
    serial,
    ModbusTransportMode.Rtu,
    new ModbusClientOptions
    {
        RtuInterFrameDelay = ModbusRtuTiming.GetInterFrameDelay(
            baudRate: 9600,
            dataBits: 8,
            hasParity: true)
    });

await client.ConnectAsync();
var result = await client.ReadInputRegistersAsync(1, 0, 4);
```

RTU 使用 CRC16/Modbus，客户端强制单请求串行化，并在交换之间应用配置的静默间隔。站号 `0` 是只写广播且没有响应，强类型客户端为避免误等待会拒绝它；当前广播可通过更低层帧 API自行发送。

## 无硬件模拟器

`ModbusTcpSimulatorServer` 提供真实 TCP 监听器，`ModbusSimulatorChannel` 提供进程内 TCP/RTU 通道。二者使用相同的 `ModbusSlave` 和 `ModbusDataStore`：

```csharp
var slave = new ModbusSlave(unitId: 1);
slave.DataStore.SetRegisters(
    ModbusDataArea.HoldingRegisters,
    address: 0,
    values: new ushort[] { 100, 200, 300 });

await using var server = new ModbusTcpSimulatorServer(
    new TcpServerOptions { ListenAddress = "127.0.0.1", Port = 5020 },
    slave,
    new ModbusSimulatorOptions
    {
        ResponseDelay = TimeSpan.FromMilliseconds(20)
    });
await server.StartAsync();
```

`ModbusSimulatorOptions` 可强制异常、丢弃响应、断开连接或破坏 RTU CRC。`ISimulationResponseScript` 可按请求序号和原始字节返回延迟、覆盖响应、超时、断线或损坏指令，该接口不依赖 Modbus，可供后续协议模拟器复用。

运行仓库示例：

```powershell
dotnet run --project samples/ServerAndSimulator -- modbus 5020
dotnet run --project samples/BasicClient -- modbus 127.0.0.1 5020
```

## 安全与重试

传统 Modbus TCP/RTU 不提供认证或加密，不应直接暴露到不可信网络。写命令的响应丢失时，自动重试可能重复执行操作；只有确认设备写入幂等或有额外事务保护时才启用写重试。
