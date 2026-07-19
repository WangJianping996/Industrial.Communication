# PLC 变量、S7 与 MC 快速入门

第五天交付统一的 `IPlcClient`、`VariableDefinition`、`PlcValueConverter` 和 `VariableMonitor`。Modbus、Siemens S7 和 Mitsubishi MC 的变量客户端使用同一套读取、写入、批量结果、质量码与时间戳模型。

所有发布类库只目标化 `netstandard2.1`；示例和测试的 `net10.0` 仅作为宿主，不进入 NuGet 包。

## 变量定义

```csharp
var speed = new VariableDefinition(
    Name: "Speed",
    Address: "DB1.DBD4",
    DataType: PlcDataType.Float32,
    Access: VariableAccess.ReadWrite,
    Scale: 0.1,
    Description: "Line speed",
    ByteOrder: PlcByteOrder.BigEndian);
```

`Length` 对数值和 Boolean 表示元素数，对 String/Bytes 表示字节容量。数值支持 `BigEndian`、`LittleEndian`、`ByteSwap` 和 `WordSwap`；字符串支持 ASCII 与 UTF-8，并按固定字节容量补零。S7 客户端会额外读写原生 STRING 的最大长度/当前长度两字节头，`Length` 范围为 1..254。

## Siemens S7

首版范围：S7-1200/1500、TCP 102、ISO-on-TCP/COTP/S7comm、DB/I/Q/M 绝对地址，以及 Bit、Byte、Int16、Int32、Float32、String 等统一数据类型。

```csharp
var channel = new TcpTransportChannel(new TcpTransportOptions
{
    Host = "192.168.0.10",
    Port = 102,
});
await using IPlcClient client = new S7PlcClient(
    new S7IsoTcpDataAccess(channel, new S7ClientOptions { Rack = 0, Slot = 1 }));
await client.ConnectAsync();

var variables = new[]
{
    new VariableDefinition("Ready", "DB1.DBX0.0", PlcDataType.Boolean),
    new VariableDefinition("Count", "DB1.DBW2", PlcDataType.Int16),
    new VariableDefinition("Speed", "DB1.DBD4", PlcDataType.Float32),
    new VariableDefinition("Label", "DB1.DBB8", PlcDataType.String, Length: 16),
};
IReadOnlyList<CommunicationResult<VariableValue>> values = await client.ReadAsync(variables);
```

地址示例：`DB1.DBX0.0`、`DB1.DBB0`、`DB1.DBW2`、`DB1.DBD4`、`I0.0`、`IB0`、`QW2`、`MD10`。首版不支持符号地址、优化 DB 的符号布局、S7-200/300/400 专属差异或完整 S7 指令集。S7-1200/1500 必须在 PLC 工程中允许相应通信访问；DB 绝对地址要求与实际非优化布局一致。

## Mitsubishi MC 3E Binary

首版范围：MC 3E Binary over TCP，批量读命令 `0401`、批量写命令 `1401`，以及 X/Y/M/D/W 设备。

```csharp
var channel = new TcpTransportChannel(new TcpTransportOptions
{
    Host = "192.168.0.20",
    Port = 5000,
});
await using IPlcClient client = new McPlcClient(new Mc3ETcpDataAccess(channel));
await client.ConnectAsync();

var variables = new[]
{
    new VariableDefinition("Ready", "M100", PlcDataType.Boolean),
    new VariableDefinition("Count", "D200", PlcDataType.Int16),
    new VariableDefinition("Speed", "D201", PlcDataType.Float32, ByteOrder: PlcByteOrder.WordSwap),
};
```

X/Y/W 设备号按十六进制解释，M/D 按十进制解释。首版不包含 4E、ASCII、UDP、随机读写和未列出的设备码。

## Modbus 统一变量适配

`ModbusPlcClient` 将已有 `ModbusClient` 适配为 `IPlcClient`。地址均为零基协议地址：`C0`（Coil）、`DI0`（Discrete Input）、`HR0`（Holding Register）、`IR0`（Input Register）。

```csharp
var raw = new ModbusClient(channel, ModbusTransportMode.Tcp);
await using IPlcClient client = new ModbusPlcClient(
    raw,
    new ModbusPlcClientOptions { UnitId = 1 });
```

## 变量监视

```csharp
await using var monitor = new VariableMonitor(client);
await monitor.StartAsync(variables, new VariableMonitorOptions
{
    PollInterval = TimeSpan.FromMilliseconds(250),
    QueueCapacity = 1024,
});

await foreach (VariableValue update in monitor.WatchAsync(cancellationToken))
{
    Console.WriteLine($"{update.Definition.Name}: {update.Value}, {update.Quality}, {update.Timestamp:O}");
}
```

各协议客户端会按区域和协议上限合并连续读取。解析失败、设备错误或单个变量转换失败只写入对应结果；监视循环把失败发布为 `Bad` 质量，不会终止其他变量。队列满时丢弃最旧更新，以免慢订阅者阻塞 PLC 轮询。

## 可替换边界

S7 和 MC 分别通过 `IS7DataAccess`、`IMcDataAccess` 隔离协议 I/O。内置实现为 `S7IsoTcpDataAccess`、`Mc3ETcpDataAccess`，测试/本地开发可使用 `S7MemoryDataAccess`、`McMemoryDataAccess`；未来接入厂商 SDK 或第三方库时不需要改变 `IPlcClient` 和变量模型。

协议依据：Siemens 的 [S7-1200/S7-1500 S7 communication 示例](https://support.industry.siemens.com/cs/attachments/82212115/82212115_S7_communication_S7-1500_S7-1200_en.pdf)，以及 Mitsubishi 的 [SLMP Reference Manual](https://dl.mitsubishielectric.com/dl/fa/document/manual/plc/sh080956eng/sh080956engn.pdf) 和 [MELSEC Communication Protocol Reference Manual](https://dl.mitsubishielectric.com/dl/fa/document/manual/plc/sh080008/sh080008ab.pdf)。
