# 传输、队列、帧处理与校验

## TCP Client

```csharp
await using var channel = new TcpTransportChannel(new TcpTransportOptions
{
    Host = "127.0.0.1",
    Port = 5020,
    ConnectTimeout = TimeSpan.FromSeconds(5),
});

await channel.ConnectAsync(cancellationToken);
await channel.SendAsync(requestBytes, cancellationToken);
await foreach (ReadOnlyMemory<byte> chunk in channel.ReceiveAsync(cancellationToken))
{
    // 将 chunk 追加到协议缓冲区，再交给 IFrameDecoder/IProtocolCodec。
}
```

TCP 的一次接收只代表一个网络数据块，不代表一个协议帧。调用方必须使用帧解码器处理半包、粘包和多帧连包。

## TCP Server

`TcpCommunicationServer` 为每个客户端生成稳定 Session ID，限制最大连接数，并通过有界请求队列施加背压。`ReadRequestsAsync` 返回原始数据块及会话信息，`SendAsync(sessionId, payload)` 向指定会话响应。

可运行示例：

```powershell
dotnet run --project samples/ServerAndSimulator -- 5020
dotnet run --project samples/BasicClient -- 127.0.0.1 5020 "hello"
```

## UDP

`UdpTransportChannel` 绑定本地端点，并可设置一个默认远端端点用于 `SendAsync`。每次接收保持 UDP 数据报边界。广播必须通过 `EnableBroadcast = true` 显式开启；组播将在后续版本补充。

在 .NET Standard 2.1 下，`UdpClient.ReceiveAsync()` 没有原生 `CancellationToken` 重载。当前实现通过关闭 Socket 保证取消及时返回，因此取消一次接收后需要断开并重连该通道。

## Serial

`SerialTransportOptions` 支持端口名、波特率、数据位、停止位、校验位、握手、驱动读写超时和接收缓冲区。实现使用 `SerialPort.BaseStream` 完成异步收发。

端口名和驱动行为取决于操作系统，例如 Windows 常见 `COM3`，Linux 常见 `/dev/ttyUSB0`。同步的 `SerialPort.Open()` 由超时包装器保护，但底层驱动不保证真正可取消。

## 有界队列

`BoundedMessageQueue<T>` 支持：

| 策略 | 满队列行为 |
|---|---|
| `Wait` | 异步等待容量，向生产者施加背压 |
| `Reject` | 拒绝新项，保留队列内容 |
| `DropOldest` | 删除最旧项并接受新项 |
| `DropNewest` | 丢弃本次写入项 |

`Complete()` 禁止后续写入，读取端仍可排空已经接受的项目。

## 帧解码器

- `FixedLengthFrameDecoder`：固定长度帧；
- `LengthFieldFrameDecoder`：1/2/4 字节长度字段、大小端、长度修正、头部剥离和最大长度；
- `DelimiterFrameDecoder`：单字节或多字节分隔符；
- `SilentIntervalFrameDecoder`：由串口接收循环在静默间隔结束时调用 `NotifySilence()`；
- `DelegatingFrameDecoder`：接入私有规则。

所有解码器返回 `Consumed` 与 `Examined`，调用方只删除 `Consumed` 字节。`NeedMoreData` 时保留全部缓冲区；`InvalidData` 时按返回值前进，避免解析死循环。

## 校验

- `Crc16Checksum` 默认使用 CRC-16/Modbus：反射多项式 `0xA001`、初值 `0xFFFF`、低字节在前；
- `LrcChecksum` 实现 Modbus ASCII 的二补数 LRC；
- `DelegatingChecksum` 接入用户算法，并检查返回长度。
