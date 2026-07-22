# Industrial Communication 使用指南

本文面向类库使用者，集中说明 `Industrial.Communication` 的安装方式、主要功能、常用 API、协议地址规则、示例代码和生产注意事项。当前包版本为 `0.1.1`。

> 类库发布资产只包含 `netstandard2.1`。它可被实现 .NET Standard 2.1 的现代 .NET 应用引用，包括 .NET 5 及后续版本；示例使用 `net10.0` 只是为了提供可运行宿主。`.NET Framework` 不支持 .NET Standard 2.1。

## 目录

- [功能概览](#功能概览)
- [选择和安装 NuGet 包](#选择和安装-nuget-包)
- [三分钟开始：Modbus TCP](#三分钟开始modbus-tcp)
- [统一的结果、取消和生命周期](#统一的结果取消和生命周期)
- [统一 PLC 变量接口](#统一-plc-变量接口)
- [Modbus TCP 和 RTU](#modbus-tcp-和-rtu)
- [Siemens S7](#siemens-s7)
- [Mitsubishi MC 3E Binary](#mitsubishi-mc-3e-binary)
- [OPC UA](#opc-ua)
- [TCP、UDP 和串口传输](#tcpudp-和串口传输)
- [帧解码、校验和队列](#帧解码校验和队列)
- [可靠通信和可观测性](#可靠通信和可观测性)
- [通用设备适配器](#通用设备适配器)
- [依赖注入](#依赖注入)
- [无硬件开发和测试](#无硬件开发和测试)
- [生产环境检查清单](#生产环境检查清单)
- [当前边界](#当前边界)

## 功能概览

| 类别 | 已提供的能力 |
|---|---|
| 通用抽象 | `CommunicationResult`、结构化错误、连接状态、异步生命周期、取消令牌 |
| 传输 | Serial、TCP Client、TCP 多会话 Server、UDP 单播和广播 |
| 报文处理 | 固定长度、长度字段、分隔符、静默间隔、自定义帧；CRC16/Modbus、LRC、自定义校验 |
| 可靠性 | 请求/响应关联、超时、重试、指数退避、自动重连、心跳和连接恢复钩子 |
| 流量控制 | 有界异步队列，支持等待、拒绝、丢最旧和丢最新策略 |
| 可观测性 | 实时报文监控、默认脱敏、滚动文件历史、过滤、CSV/JSON 导出和定时回放 |
| Modbus | TCP/RTU，功能码 01、02、03、04、05、06、15、16，客户端和模拟器 |
| Siemens S7 | S7-1200/1500、ISO-on-TCP/COTP/S7comm、DB/I/Q/M 绝对地址 |
| Mitsubishi MC | 3E Binary over TCP，X/Y/M/D/W 软元件批量读写 |
| OPC UA | 官方 SDK、端点发现、认证、证书、读写、订阅、重连和订阅恢复 |
| 统一 PLC 模型 | `IPlcClient`、变量定义、类型转换、缩放、字节序、批量读写、变量监视 |
| 设备适配 | 数字 IO、运动控制、扫码器、称重设备、私有 ASCII/二进制协议和厂商 SDK 桥接 |
| 测试替身 | Modbus 模拟服务器/通道、S7/MC 内存访问、OPC UA 内存会话和故障注入 |
| 应用集成 | Microsoft.Extensions.DependencyInjection 注册扩展 |

## 选择和安装 NuGet 包

按实际需求安装，不必为了使用 TCP 或 Modbus 引入 OPC UA SDK。

| NuGet 包 | 适用场景 |
|---|---|
| `Industrial.Communication.Abstractions` | 只需要公共接口、结果和模型 |
| `Industrial.Communication.Core` | 帧、校验、队列、可靠性、监控、历史、导出和回放 |
| `Industrial.Communication.Transports` | Serial、TCP、UDP |
| `Industrial.Communication.Protocols.Modbus` | Modbus TCP/RTU 和模拟器 |
| `Industrial.Communication.Protocols.S7` | Siemens S7 ISO-on-TCP |
| `Industrial.Communication.Protocols.Mc` | Mitsubishi MC 3E Binary |
| `Industrial.Communication.Protocols.OpcUa` | OPC UA 官方 SDK 会话 |
| `Industrial.Communication.Adapters` | 通用设备适配器 |
| `Industrial.Communication.DependencyInjection` | 一次注册 Modbus、S7、MC、OPC UA 客户端 |

例如，创建应用并安装 Modbus 包：

```powershell
dotnet new console -n IndustrialDemo -f net10.0
cd IndustrialDemo
dotnet add package Industrial.Communication.Protocols.Modbus --version 0.1.1
```

也可以直接写入项目文件：

```xml
<ItemGroup>
  <PackageReference Include="Industrial.Communication.Protocols.Modbus"
                    Version="0.1.1" />
</ItemGroup>
```

协议包会通过 NuGet 依赖关系带入它所需的 Core、Transports 和 Abstractions 包。

## 三分钟开始：Modbus TCP

下面是一个完整客户端示例。所有公开 I/O API 都是异步的，并支持 `CancellationToken`。

```csharp
using Communication.Abstractions.Models;
using Communication.Protocols.Modbus.Client;
using Communication.Protocols.Modbus.Models;
using Communication.Transports.Options;
using Communication.Transports.Tcp;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

var channel = new TcpTransportChannel(new TcpTransportOptions
{
    Host = "192.168.1.20",
    Port = 502,
    ConnectTimeout = TimeSpan.FromSeconds(3)
});

await using var client = new ModbusClient(channel, ModbusTransportMode.Tcp);

CommunicationResult connected = await client.ConnectAsync(timeout.Token);
if (!connected.IsSuccess)
{
    Console.Error.WriteLine($"{connected.Error!.Code}: {connected.Error.Message}");
    return;
}

CommunicationResult<IReadOnlyList<ushort>> read =
    await client.ReadHoldingRegistersAsync(
        unitId: 1,
        address: 0,
        quantity: 10,
        cancellationToken: timeout.Token);

if (read.IsSuccess)
{
    Console.WriteLine(string.Join(", ", read.Value!));
}
else
{
    Console.Error.WriteLine($"{read.Error!.Code}: {read.Error.Message}");
}

await client.DisconnectAsync();
```

## 统一的结果、取消和生命周期

### 结果而不是异常分支

可预期的通信失败通过 `CommunicationResult` 或 `CommunicationResult<T>` 返回：

```csharp
CommunicationResult<VariableValue> result = await plc.ReadAsync(variable, cancellationToken);

if (!result.IsSuccess)
{
    CommunicationError error = result.Error!;
    Console.Error.WriteLine($"code={error.Code}, message={error.Message}, detail={error.Detail}");
    return;
}

VariableValue value = result.Value!;
```

常见错误码包括 `Timeout`、`Canceled`、`ConnectionFailure`、`ProtocolError`、`ChecksumFailure`、`DeviceError`、`InvalidAddress`、`InvalidValue`、`QueueFull`、`InvalidState` 和 `StorageFailure`。

当失败应直接中断当前业务时，可以使用：

```csharp
VariableValue value = result.GetValueOrThrow();
```

参数为空、对象已释放等编程错误仍可能抛出标准 .NET 异常。主动取消通常继续以 `OperationCanceledException` 传播，因此上层应按正常取消流程处理。

### 生命周期

通道、客户端、订阅、监视器和设备适配器都应被释放：

```csharp
await using var client = CreateClient();

CommunicationResult connected = await client.ConnectAsync(cancellationToken);
if (!connected.IsSuccess)
{
    return;
}

// 读写……

await client.DisconnectAsync(cancellationToken);
```

连接状态为 `Disconnected`、`Connecting`、`Connected`、`Reconnecting`、`Disconnecting` 或 `Faulted`。不要在多个地方重复释放同一实例；使用 DI 时通常让容器管理 Singleton 的释放。

## 统一 PLC 变量接口

Modbus、S7、MC 和 OPC UA 都实现 `IPlcClient`，上层业务可使用同一套变量模型。

### 定义变量

```csharp
using Communication.Abstractions.Models;

var speed = new VariableDefinition(
    Name: "LineSpeed",
    Address: "DB1.DBD4",
    DataType: PlcDataType.Float32,
    Length: 1,
    Access: VariableAccess.ReadWrite,
    Scale: 0.1,
    Description: "产线速度",
    ByteOrder: PlcByteOrder.BigEndian,
    StringEncoding: PlcStringEncoding.Ascii);
```

支持的类型为 `Boolean`、`Byte`、`Int16`、`UInt16`、`Int32`、`UInt32`、`Float32`、`Float64`、`String` 和 `Bytes`。字节序支持 `BigEndian`、`LittleEndian`、`ByteSwap`、`WordSwap`；字符串支持 ASCII 和 UTF-8。

`Scale` 是应用到原始数值的乘数。例如寄存器原值 `215`、`Scale: 0.1` 会得到 `21.5`。写入时会执行相反换算。

### 单变量和批量读写

```csharp
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

IPlcClient plc = CreatePlcClient();
await using (plc)
{
    CommunicationResult connected = await plc.ConnectAsync(cancellationToken);
    if (!connected.IsSuccess)
    {
        return;
    }

    var ready = new VariableDefinition("Ready", "C0", PlcDataType.Boolean);
    var count = new VariableDefinition("Count", "HR10", PlcDataType.Int16);

    CommunicationResult<VariableValue> one = await plc.ReadAsync(count, cancellationToken);

    IReadOnlyList<CommunicationResult<VariableValue>> batch =
        await plc.ReadAsync(new[] { ready, count }, cancellationToken);

    CommunicationResult write = await plc.WriteAsync(
        new PlcWriteRequest(count, (short)123),
        cancellationToken);

    foreach (CommunicationResult<VariableValue> item in batch)
    {
        Console.WriteLine(item.IsSuccess
            ? $"{item.Value!.Definition.Name}={item.Value.Value}"
            : $"读取失败：{item.Error!.Message}");
    }
}
```

批量 API 返回逐变量结果。一个地址失败不会覆盖其他地址的成功结果；客户端会按照协议区域和单次请求上限合并连续地址。

`VariableValue` 包含值、`Good/Uncertain/Bad` 质量、时间戳和可选的逐变量错误。

### 周期监视

```csharp
using Communication.Core.Plc;

await using var monitor = new VariableMonitor(plc);
CommunicationResult started = await monitor.StartAsync(
    new[] { ready, count },
    new VariableMonitorOptions
    {
        PollInterval = TimeSpan.FromMilliseconds(250),
        PublishUnchangedValues = false,
        QueueCapacity = 1024
    },
    cancellationToken);

if (!started.IsSuccess)
{
    return;
}

await foreach (VariableValue update in monitor.WatchAsync(cancellationToken))
{
    Console.WriteLine(
        $"{update.Definition.Name}={update.Value}; " +
        $"quality={update.Quality}; time={update.Timestamp:O}");
}
```

默认只发布变化。队列满时会丢弃最旧更新，避免慢订阅者阻塞 PLC 轮询。

## Modbus TCP 和 RTU

安装：

```powershell
dotnet add package Industrial.Communication.Protocols.Modbus --version 0.1.1
```

### 地址规则

所有 Modbus API 使用 **0 基协议地址**，不直接使用设备手册中的展示编号：

| 手册编号 | 数据区 | 本库地址/API |
|---|---|---|
| `00001` | Coil | 地址 `0` / 变量地址 `C0` |
| `10001` | Discrete Input | 地址 `0` / `DI0` |
| `30001` | Input Register | 地址 `0` / `IR0` |
| `40001` | Holding Register | 地址 `0` / `HR0` |

厂商手册也可能直接使用 0 基偏移，接入前必须确认；类库不会猜测或自动减一。

### 支持的功能码

| 功能码 | API | 单次上限 |
|---:|---|---:|
| 01 | `ReadCoilsAsync` | 2000 bits |
| 02 | `ReadDiscreteInputsAsync` | 2000 bits |
| 03 | `ReadHoldingRegistersAsync` | 125 registers |
| 04 | `ReadInputRegistersAsync` | 125 registers |
| 05 | `WriteSingleCoilAsync` | 1 coil |
| 06 | `WriteSingleRegisterAsync` | 1 register |
| 15 | `WriteMultipleCoilsAsync` | 1968 coils |
| 16 | `WriteMultipleRegistersAsync` | 123 registers |

### TCP 读写

```csharp
var values = await client.ReadHoldingRegistersAsync(
    1, 0, 10, cancellationToken: cancellationToken);
var writeOne = await client.WriteSingleRegisterAsync(
    1, 10, 123, cancellationToken: cancellationToken);
var writeMany = await client.WriteMultipleRegistersAsync(
    1,
    20,
    new ushort[] { 100, 200, 300 },
    cancellationToken: cancellationToken);
```

TCP 使用 MBAP 事务 ID，可以配置并发请求并关联乱序响应。Modbus 异常响应会转换为 `DeviceError`，原始异常码保存在 `CommunicationError.Detail`。

### RTU 串口

```csharp
using System.IO.Ports;
using Communication.Protocols.Modbus.Client;
using Communication.Protocols.Modbus.Models;
using Communication.Transports.Options;
using Communication.Transports.Serial;

var serial = new SerialTransportChannel(new SerialTransportOptions
{
    PortName = "COM3",             // Linux 示例：/dev/ttyUSB0
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

await client.ConnectAsync(cancellationToken);
CommunicationResult<IReadOnlyList<ushort>> result =
    await client.ReadInputRegistersAsync(
        1, 0, 4, cancellationToken: cancellationToken);
```

RTU 自动使用 CRC16/Modbus、串行化请求并等待静默间隔。强类型客户端拒绝站号 `0`，避免对广播写入错误地等待响应。

### 作为统一 PLC 客户端

```csharp
var rawClient = new ModbusClient(channel, ModbusTransportMode.Tcp);
await using IPlcClient plc = new ModbusPlcClient(
    rawClient,
    new ModbusPlcClientOptions { UnitId = 1 });
```

## Siemens S7

安装：

```powershell
dotnet add package Industrial.Communication.Protocols.S7 --version 0.1.1
```

首版支持 S7-1200/1500、TCP 102、ISO-on-TCP/COTP/S7comm，以及 DB/I/Q/M 的绝对地址。

```csharp
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Protocols.S7;
using Communication.Protocols.S7.Models;
using Communication.Transports.Options;
using Communication.Transports.Tcp;

var channel = new TcpTransportChannel(new TcpTransportOptions
{
    Host = "192.168.0.10",
    Port = 102
});

await using IPlcClient client = new S7PlcClient(
    new S7IsoTcpDataAccess(
        channel,
        new S7ClientOptions { Rack = 0, Slot = 1 }));

await client.ConnectAsync(cancellationToken);

var variables = new[]
{
    new VariableDefinition("Ready", "DB1.DBX0.0", PlcDataType.Boolean),
    new VariableDefinition("Count", "DB1.DBW2", PlcDataType.Int16),
    new VariableDefinition("Speed", "DB1.DBD4", PlcDataType.Float32),
    new VariableDefinition("Label", "DB1.DBB8", PlcDataType.String, Length: 16)
};

IReadOnlyList<CommunicationResult<VariableValue>> values =
    await client.ReadAsync(variables, cancellationToken);
```

地址示例：`DB1.DBX0.0`、`DB1.DBB0`、`DB1.DBW2`、`DB1.DBD4`、`I0.0`、`IB0`、`QW2`、`MD10`。

S7 原生 STRING 使用最大长度和当前长度两字节头，`Length` 有效范围为 1..254。S7-1200/1500 需要在 PLC 工程中允许通信访问；绝对 DB 地址必须与实际非优化布局一致。

## Mitsubishi MC 3E Binary

安装：

```powershell
dotnet add package Industrial.Communication.Protocols.Mc --version 0.1.1
```

首版提供 MC 3E Binary over TCP，支持批量读命令 `0401`、批量写命令 `1401` 和 X/Y/M/D/W 软元件。

```csharp
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Protocols.Mc;
using Communication.Transports.Options;
using Communication.Transports.Tcp;

var channel = new TcpTransportChannel(new TcpTransportOptions
{
    Host = "192.168.0.20",
    Port = 5000 // 以 PLC 参数为准
});

await using IPlcClient client = new McPlcClient(new Mc3ETcpDataAccess(channel));
await client.ConnectAsync(cancellationToken);

var variables = new[]
{
    new VariableDefinition("Ready", "M100", PlcDataType.Boolean),
    new VariableDefinition("Count", "D200", PlcDataType.Int16),
    new VariableDefinition(
        "Speed",
        "D201",
        PlcDataType.Float32,
        ByteOrder: PlcByteOrder.WordSwap)
};

IReadOnlyList<CommunicationResult<VariableValue>> values =
    await client.ReadAsync(variables, cancellationToken);
```

X/Y/W 设备号按十六进制解释，M/D 按十进制解释。端口号不是协议固定值，应以 PLC 网络参数为准。

## OPC UA

安装：

```powershell
dotnet add package Industrial.Communication.Protocols.OpcUa --version 0.1.1
```

### 安全连接

生产环境建议使用 `SignAndEncrypt + Basic256Sha256`，并建立可信证书库：

```csharp
using Communication.Protocols.OpcUa;
using Communication.Protocols.OpcUa.Models;

var options = new OpcUaConnectionOptions
{
    EndpointUrl = "opc.tcp://server:4840",
    SecurityMode = OpcUaMessageSecurityMode.SignAndEncrypt,
    SecurityPolicy = OpcUaSecurityPolicy.Basic256Sha256,
    Identity = new OpcUaIdentityOptions
    {
        Kind = OpcUaIdentityKind.UsernamePassword,
        Username = "operator",
        Password = passwordFromSecretStore
    },
    Certificates = new OpcUaCertificateOptions
    {
        ApplicationCertificateStorePath = "pki/own",
        TrustedPeerStorePath = "pki/trusted",
        TrustedIssuerStorePath = "pki/issuer",
        RejectedCertificateStorePath = "pki/rejected",
        AllowUntrustedCertificates = false
    }
};

await using var session = new OpcFoundationSession(options);
await using var client = new OpcUaClient(session);

client.SecurityWarning += (_, warning) =>
    Console.Error.WriteLine($"OPC UA 安全警告：{warning.Message}");

CommunicationResult connected = await client.ConnectAsync(cancellationToken);
```

默认拒绝不受信任的服务器证书。首次连接失败时，从 `pki/rejected` 审核服务器证书，确认指纹和来源后再放入 `pki/trusted`。不要在生产环境把 `AllowUntrustedCertificates` 设为 `true`，客户端也不会自动降级到 `None`。

### 读、写和订阅

```csharp
var temperature = new VariableDefinition(
    "Temperature",
    "ns=2;s=Temperature",
    PlcDataType.Float64,
    Scale: 0.1);

CommunicationResult<VariableValue> read =
    await client.ReadAsync(temperature, cancellationToken);

CommunicationResult written = await client.WriteAsync(
    new PlcWriteRequest(temperature, 25.5),
    cancellationToken);

CommunicationResult<OpcUaVariableSubscription> created =
    await client.SubscribeAsync(
        new[] { temperature },
        new OpcUaSubscriptionOptions
        {
            PublishingInterval = TimeSpan.FromMilliseconds(250),
            SamplingInterval = TimeSpan.FromMilliseconds(250),
            MonitoredItemQueueSize = 1,
            UpdateQueueCapacity = 1024
        },
        cancellationToken);

await using OpcUaVariableSubscription subscription = created.GetValueOrThrow();
await foreach (VariableValue update in subscription.WatchAsync(cancellationToken))
{
    Console.WriteLine($"{update.Value}, {update.Quality}, {update.Timestamp:O}");
}
```

坏 KeepAlive 会触发持续重连。重连成功后订阅会被重新创建，`subscription.RestoreCount` 可用于运行状态检查。质量和 OPC UA source timestamp 会映射到统一 `VariableValue`。

## TCP、UDP 和串口传输

安装：

```powershell
dotnet add package Industrial.Communication.Transports --version 0.1.1
```

### 原始 TCP Client

```csharp
using System.Text;
using Communication.Transports.Options;
using Communication.Transports.Tcp;

await using var channel = new TcpTransportChannel(new TcpTransportOptions
{
    Host = "127.0.0.1",
    Port = 5020,
    ConnectTimeout = TimeSpan.FromSeconds(5),
    NoDelay = true
});

await channel.ConnectAsync(cancellationToken);
await channel.SendAsync(Encoding.UTF8.GetBytes("hello"), cancellationToken);

await foreach (ReadOnlyMemory<byte> chunk in channel.ReceiveAsync(cancellationToken))
{
    Console.WriteLine(Encoding.UTF8.GetString(chunk.Span));
    break;
}
```

一次 TCP 接收只代表一个网络数据块，不代表一个完整业务帧。私有协议必须使用帧解码器处理半包、粘包和多帧连包。

### TCP 多会话 Server

```csharp
var serverOptions = new TcpServerOptions
{
    ListenAddress = "0.0.0.0",
    Port = 5020,
    MaxConnections = 100,
    RequestQueueCapacity = 1024
};

await using var server = new TcpCommunicationServer(serverOptions);
CommunicationResult started = await server.StartAsync(cancellationToken);

await foreach (ServerRequestContext request in server.ReadRequestsAsync(cancellationToken))
{
    await server.SendAsync(
        request.Session.SessionId,
        request.Message.Payload,
        cancellationToken);
}
```

Server 为每个连接生成稳定 Session ID，并限制最大连接数和请求队列容量。

### UDP

```csharp
using Communication.Transports.Udp;

await using var udp = new UdpTransportChannel(new UdpTransportOptions
{
    LocalAddress = "0.0.0.0",
    LocalPort = 0,
    RemoteHost = "192.168.1.50",
    RemotePort = 9000,
    EnableBroadcast = false
});

await udp.ConnectAsync(cancellationToken);
await udp.SendAsync(new byte[] { 0x01, 0x02 }, cancellationToken);
```

UDP 接收保留数据报边界。广播必须显式设置 `EnableBroadcast = true`。在 .NET Standard 2.1 下，取消一次等待中的 UDP 接收会通过关闭 Socket 实现，随后需要断开并重新连接通道。

### 串口

```csharp
using System.IO.Ports;
using Communication.Transports.Options;
using Communication.Transports.Serial;

await using var serial = new SerialTransportChannel(new SerialTransportOptions
{
    PortName = "COM3",
    BaudRate = 115200,
    DataBits = 8,
    Parity = Parity.None,
    StopBits = StopBits.One,
    Handshake = Handshake.None,
    OpenTimeout = TimeSpan.FromSeconds(5),
    ReadTimeout = TimeSpan.FromSeconds(5),
    WriteTimeout = TimeSpan.FromSeconds(5)
});

await serial.ConnectAsync(cancellationToken);
await serial.SendAsync(new byte[] { 0x02, 0x03 }, cancellationToken);
```

串口名称、权限和驱动行为取决于操作系统。Linux 进程通常还需要访问 `/dev/ttyUSB*` 的权限。

## 帧解码、校验和队列

这些功能位于 `Industrial.Communication.Core`。

### 选择帧解码器

```csharp
using Communication.Core.Framing;

var fixedLength = new FixedLengthFrameDecoder(frameLength: 16);

var lineBased = new DelimiterFrameDecoder(
    delimiter: new byte[] { 0x0D, 0x0A },
    includeDelimiter: false,
    maxFrameLength: 4096);

var lengthField = new LengthFieldFrameDecoder(
    lengthFieldOffset: 2,
    lengthFieldLength: 2,
    lengthAdjustment: 4,
    initialBytesToStrip: 0,
    littleEndian: false,
    maxFrameLength: 65535);
```

还提供 `SilentIntervalFrameDecoder`（串口静默间隔成帧）和 `DelegatingFrameDecoder`（自定义规则）。增量解码结果包含 `NeedMoreData`、`Done`、`InvalidData` 以及 `Consumed/Examined`，调用方只应删除 `Consumed` 指定的字节。

### 校验

```csharp
using Communication.Core.Checksums;

var crc = new Crc16Checksum(); // 默认 CRC-16/Modbus
ReadOnlyMemory<byte> crcBytes = crc.Compute(payload);
bool crcOk = crc.Validate(payload, receivedCrc);

var lrc = new LrcChecksum();
ReadOnlyMemory<byte> lrcBytes = lrc.Compute(payload);
```

私有算法可使用 `DelegatingChecksum`。

### 有界队列和背压

```csharp
using Communication.Abstractions.Models;
using Communication.Core.Queues;

await using var queue = new BoundedMessageQueue<byte[]>(
    capacity: 100,
    backpressureStrategy: QueueBackpressureStrategy.DropOldest,
    singleReader: true);

QueueWriteResult written = await queue.WriteAsync(payload, cancellationToken);

await foreach (byte[] item in queue.ReadAllAsync(cancellationToken))
{
    Process(item);
}

queue.Complete();
```

策略含义：`Wait` 向生产者施加背压，`Reject` 保留队列内容并拒绝新项，`DropOldest` 丢最旧项后接收新项，`DropNewest` 丢当前新项。

## 可靠通信和可观测性

### 请求可靠性

`ReliableCommunicationClient<TRequest,TResponse>` 可组合传输通道、协议编解码器和响应关联器：

- 无事务 ID 的协议使用 `SingleRequestCorrelator`，一次只允许一个在途请求。
- 有事务 ID 的协议使用 `DelegatingResponseCorrelator`，支持并发和乱序响应。
- `CommunicationRequestOptions.Timeout` 控制单次尝试超时。
- `ExponentialBackoffRetryPolicy` 支持最大次数、最大延迟、倍率、抖动和可重试错误判断。
- `ExponentialBackoffReconnectPolicy` 和 `AutomaticReconnectCoordinator` 提供自动重连。
- `IConnectionRecoveryHandler` 用于恢复订阅或监控。
- `HeartbeatService` 执行协议专用心跳，并在连续失败时发出事件。

```csharp
var retry = new ExponentialBackoffRetryPolicy(
    maxAttempts: 3,
    initialDelay: TimeSpan.FromMilliseconds(100),
    maxDelay: TimeSpan.FromSeconds(2));
```

`MaxAttempts` 包含首次调用。写请求只有在设备写入具备幂等性或额外事务保护时才应自动重试；响应丢失可能导致设备已执行但客户端再次写入。

### 实时报文监控和历史

```csharp
using Communication.Core.Monitoring;
using Communication.Core.Storage;

await using var monitor = new MessageMonitor();
// 创建协议客户端时把同一个 monitor 传入，例如：
// await using var client = new ModbusClient(channel, ModbusTransportMode.Tcp, monitor: monitor);
await using var store = new FileMessageStore(new FileMessageStoreOptions
{
    DirectoryPath = Path.Combine(appDataPath, "communication-history"),
    RollFileSizeBytes = 16 * 1024 * 1024,
    MaxTotalSizeBytes = 256 * 1024 * 1024,
    RetentionPeriod = TimeSpan.FromDays(7)
});

await using var writer = new MessageStoreWriter(monitor, store);
writer.PersistenceFailed += (_, args) =>
    Console.Error.WriteLine(args.Error.Message);
writer.Start();
```

`MessageMonitor` 为每个订阅者建立有界缓冲区；慢订阅者只会丢自己的最旧记录，不会阻塞主链路。默认 `SuppressPayloadMessageRedactor` 不输出原始载荷。只有明确评估敏感数据风险后，才使用 `PassThroughMessageRedactor`。

### 查询和导出

```csharp
using Communication.Abstractions.Models;
using Communication.Core.Export;

var filter = new MessageFilter
{
    From = DateTimeOffset.UtcNow.AddHours(-1),
    Protocol = "modbus",
    Direction = MessageDirection.Inbound
};

await using var output = File.Create("messages.csv");
var exporter = new MessageExporter();
CommunicationResult<long> exported = await exporter.ExportAsync(
    store.QueryAsync(filter, cancellationToken),
    output,
    new MessageExportOptions("csv", IncludePayload: false),
    cancellationToken);
```

导出格式支持 `csv` 和 `json`。默认不导出载荷；`IncludePayload: true` 会以 Base64 写入。目标流由调用方拥有。

### 定时回放

```csharp
using Communication.Core.Replay;

var replay = new MessageReplayService();
await foreach (MessageEnvelope message in replay.ReplayAsync(
    store.QueryAsync(filter, cancellationToken),
    new MessageReplayOptions
    {
        TimingMode = ReplayTimingMode.OriginalIntervals,
        Speed = 2.0,
        Direction = MessageDirection.Inbound
    },
    cancellationToken))
{
    Console.WriteLine($"{message.Timestamp:O} {message.Summary}");
}
```

回放只生成定时消息流，不会自动写入真实设备。若业务选择把回放消息发送到设备，必须另外实现权限、互锁、急停和危险动作拦截。

## 通用设备适配器

安装：

```powershell
dotnet add package Industrial.Communication.Adapters --version 0.1.1
```

### 数字 IO

把厂商 SDK 的读取和写入委托包装成稳定公共接口：

```csharp
using Communication.Abstractions.Models;
using Communication.Adapters;

bool input = false;

await using var io = new DelegateDigitalIoAdapter(
    "line-1-io",
    cancellationToken => ValueTask.FromResult(
        CommunicationResult<DigitalIoSnapshot>.Success(
            new DigitalIoSnapshot(
                new[] { input },
                new[] { false },
                DateTimeOffset.UtcNow))),
    (index, value, cancellationToken) =>
    {
        VendorSdkWriteOutput(index, value);
        return ValueTask.FromResult(CommunicationResult.Success());
    });

io.InputChanged += (_, edge) =>
    Console.WriteLine($"DI{edge.Index}: {edge.PreviousValue} -> {edge.CurrentValue}");

await io.StartAsync(cancellationToken);
await io.ReadStatusAsync(cancellationToken);
await io.SetOutputAsync(0, true, cancellationToken);
```

### 其他适配器

| 类型 | 能力 |
|---|---|
| `DelegateMotionControllerAdapter` | 轴状态、使能、回零、绝对/相对运动和停止 |
| `BarcodeScannerAdapter` | 分帧、扫码事件、时间窗去重和显式触发命令 |
| `WeighingDeviceAdapter` | 稳定状态、毛重、净重、单位、连续读数、去皮和置零 |
| `DelegateFramedDeviceAdapter<TReading>` | 组合通道、帧解码器和解析委托，接入私有 ASCII/二进制设备 |
| `DelegateDeviceAdapter` | 只桥接厂商 SDK 的启动、停止和健康检查 |

厂商 SDK 类型应只存在于委托闭包或应用内部，不要泄漏到公共接口。运动、使能、回零等危险动作不会在启动或重连后自动保存、排队或重放。

## 依赖注入

安装：

```powershell
dotnet add package Industrial.Communication.DependencyInjection --version 0.1.1
```

在 ASP.NET Core、Worker Service 或 Generic Host 中注册：

```csharp
using Communication.Abstractions.Interfaces;
using Communication.DependencyInjection;
using Communication.Protocols.Modbus.Models;
using Communication.Protocols.OpcUa.Models;
using Communication.Transports.Options;
using Communication.Transports.Tcp;
using Microsoft.Extensions.DependencyInjection;

IServiceCollection services = new ServiceCollection();

services.AddModbusPlcClient(
    _ => new TcpTransportChannel(new TcpTransportOptions
    {
        Host = "192.168.1.20",
        Port = 502
    }),
    ModbusTransportMode.Tcp);

services.AddOpcUaPlcClient(_ => new OpcUaConnectionOptions
{
    EndpointUrl = "opc.tcp://server:4840"
});

await using ServiceProvider provider = services.BuildServiceProvider();
IEnumerable<IPlcClient> clients = provider.GetServices<IPlcClient>();
```

还提供 `AddS7PlcClient` 和 `AddMc3EPlcClient`。默认生命周期为 Singleton；同一类型可按需更改 `ServiceLifetime`。通过 `IEnumerable<IPlcClient>` 可以解析多个协议客户端。

如果需要按设备标识区分同协议的多个连接，建议在应用层增加客户端注册表或工厂，不要依赖集合顺序表达设备身份。

## 无硬件开发和测试

### Modbus TCP 模拟服务器

```csharp
using Communication.Protocols.Modbus.Models;
using Communication.Protocols.Modbus.Simulator;
using Communication.Transports.Options;

var slave = new ModbusSlave(unitId: 1);
slave.DataStore.SetRegisters(
    ModbusDataArea.HoldingRegisters,
    address: 0,
    values: new ushort[] { 100, 200, 300 });

await using var server = new ModbusTcpSimulatorServer(
    new TcpServerOptions
    {
        ListenAddress = "127.0.0.1",
        Port = 5020
    },
    slave,
    new ModbusSimulatorOptions
    {
        ResponseDelay = TimeSpan.FromMilliseconds(20)
    });

await server.StartAsync(cancellationToken);
```

`ModbusSimulatorChannel` 可直接在进程内模拟 TCP/RTU。模拟器支持延迟、丢响应、断开、损坏帧和设备异常等故障注入。

### S7、MC 和 OPC UA 内存替身

```csharp
var s7 = new S7PlcClient(new S7MemoryDataAccess());
var mc = new McPlcClient(new McMemoryDataAccess());

var opcOptions = new OpcUaConnectionOptions
{
    EndpointUrl = "opc.tcp://localhost:4840"
};
await using var opcSession = new MemoryOpcUaSession(
    opcOptions,
    new Dictionary<string, object?>
    {
        ["ns=2;s=Temperature"] = 21.5d
    });
var opc = new OpcUaClient(opcSession);
```

这些替身适合单元测试、演示、变量映射验证和断线恢复测试，不代替真实 PLC/服务器互操作性验收。

仓库内可直接运行：

```powershell
dotnet run --project samples/ServerAndSimulator -- modbus 5020
dotnet run --project samples/BasicClient -- modbus 127.0.0.1 5020
dotnet run --project samples/PlcVariableMonitor
dotnet run --project samples/OpcUaAndDevices
```

## 生产环境检查清单

- 为每次连接、读取、写入和关闭传入可取消的 `CancellationToken`，并设置合理超时。
- 使用 `await using` 或 DI 容器管理客户端生命周期。
- 逐项检查批量读写结果，不要只检查集合是否返回。
- 接入前核对 PLC 型号、固件、端口、站号、Rack/Slot、地址基准、字节序和数据块布局。
- Modbus、S7 和 MC 不自带认证与加密，应部署在分区、受控的工业网络中，不直接暴露到互联网。
- OPC UA 生产环境启用签名和加密，审核服务器证书并使用密钥存储提供账号密码。
- 原始报文记录默认保持脱敏；启用载荷记录前完成隐私、凭据和存储权限评估。
- 非幂等写入默认不要自动重试；危险动作不要自动回放或在重连后恢复执行。
- 历史目录应专用、受限并纳入磁盘空间监控。
- 上线前使用目标硬件完成并发、断网、重启、超时、乱码、证书更新和长时间稳定性测试。

## 当前边界

- 发布类库为 `netstandard2.1` 单目标；`.NET Framework` 不兼容。
- Serial 已实现，但仍应在目标 Windows/Linux 串口和驱动上验收。
- UDP 暂无组播支持。
- Modbus 暂不含功能码 22/23、ASCII、Modbus Security 和强类型 RTU 广播封装。
- S7 暂不含符号地址、优化 DB 符号布局、S7-200/300/400 专属差异和完整指令集。
- MC 暂不含 4E、ASCII、UDP、随机读写和全部软元件码。
- OPC UA 已完成内存会话测试，真实服务器、证书部署和厂商互操作性仍需现场验证。
- 文件历史面向单进程，不提供多进程共享、数据库索引或静态加密。
- 有界监控在过载时允许丢失观测消息，以保护主通信链路。

更细的协议和设计说明见：

- [快速开始](getting-started.md)
- [传输与帧处理](transport-guide.md)
- [可靠性与可观测性](reliability-and-observability.md)
- [Modbus 指南](modbus-guide.md)
- [PLC、S7 与 MC 指南](plc-variable-guide.md)
- [OPC UA 与设备适配器](opcua-and-device-adapters.md)
- [安全说明](security.md)
- [故障排查](troubleshooting.md)
- [支持矩阵](supported-features.md)
- [API 参考](api-reference.md)
