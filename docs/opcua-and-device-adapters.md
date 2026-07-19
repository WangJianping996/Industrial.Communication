# OPC UA 与通用设备适配器

第六天交付包含独立 OPC UA NuGet 包和不依赖厂商 SDK 的设备契约。所有发布类库只生成 `netstandard2.1` 资产；示例和测试使用 `net10.0`。

## OPC UA 快速开始

生产环境使用官方 OPC Foundation SDK 会话：

```csharp
var options = new OpcUaConnectionOptions
{
    EndpointUrl = "opc.tcp://server:4840",
    SecurityMode = OpcUaMessageSecurityMode.SignAndEncrypt,
    SecurityPolicy = OpcUaSecurityPolicy.Basic256Sha256,
    Identity = new OpcUaIdentityOptions
    {
        Kind = OpcUaIdentityKind.UsernamePassword,
        Username = "operator",
        Password = secret,
    },
    Certificates = new OpcUaCertificateOptions
    {
        ApplicationCertificateStorePath = "pki/own",
        TrustedPeerStorePath = "pki/trusted",
        TrustedIssuerStorePath = "pki/issuer",
        RejectedCertificateStorePath = "pki/rejected",
    },
};

await using var session = new OpcFoundationSession(options);
var client = new OpcUaClient(session);
client.SecurityWarning += (_, warning) => logger.LogWarning("{Warning}", warning.Message);
CommunicationResult connected = await client.ConnectAsync(cancellationToken);
```

默认 `AllowUntrustedCertificates` 为 `false`，未进入信任库的服务器证书会被拒绝并写入 rejected 目录。只有调用方显式设置为 `true` 时才接受不受信任证书，并触发 `SecurityWarning`；该选项只适合受控调试，生产环境应把服务器证书加入 trusted 目录。

端点必须同时匹配请求的消息安全模式和安全策略，不会悄悄降级到 `None`。`DiscoverAsync` 可在建会话前列出服务器端点和支持的用户身份。

统一变量读写与订阅：

```csharp
var temperature = new VariableDefinition(
    "Temperature", "ns=2;s=Temperature", PlcDataType.Float64, Scale: 0.1);

CommunicationResult<VariableValue> value = await client.ReadAsync(temperature, cancellationToken);
await client.WriteAsync(new PlcWriteRequest(temperature, 25.5), cancellationToken);

CommunicationResult<OpcUaVariableSubscription> result = await client.SubscribeAsync(
    new[] { temperature },
    new OpcUaSubscriptionOptions { PublishingInterval = TimeSpan.FromMilliseconds(250) },
    cancellationToken);
await using OpcUaVariableSubscription subscription = result.GetValueOrThrow();
await foreach (VariableValue update in subscription.WatchAsync(cancellationToken))
{
    // update.Value, update.Quality and OPC UA source timestamp are already mapped.
}
```

官方会话检测到坏 KeepAlive 后会按 `ReconnectDelay` 持续恢复。恢复成功会发出内部重连事件，`OpcUaVariableSubscription` 随后创建新 Subscription/MonitoredItem，再释放旧订阅。`RestoreCount` 可用于运行状态检查。

测试和离线演示可使用 `MemoryOpcUaSession`。它能模拟服务端在断线时丢失全部订阅，以验证重建逻辑，而不需要安装 OPC UA 服务器。

## 通用设备适配器

- `DelegateDigitalIoAdapter`：输入/输出快照、逐点与批量输出、输入边沿事件。
- `DelegateMotionControllerAdapter`：轴状态、使能、回零、绝对/相对运动和停止。所有动作必须显式调用，生命周期启动或重连不会保存、排队或重放动作。
- `BarcodeScannerAdapter`：配合分隔符或长度帧解码器，提供扫码事件、时间窗重复过滤与显式触发命令。
- `WeighingDeviceAdapter`：由解析委托映射稳定状态、毛重、净重、单位，并提供连续结果、去皮和置零命令。
- `DelegateFramedDeviceAdapter<TReading>`：组合 `ITransportChannel`、`IFrameDecoder` 和 lambda，适配私有 ASCII/二进制帧。
- `DelegateDeviceAdapter`：只需桥接私有 SDK 的启动、停止和健康检查时使用。

厂商 SDK 对象应只由委托闭包持有，不应出现在 `IDigitalIoDevice`、`IMotionController`、`IBarcodeScanner`、`IWeighingDevice` 或其他公共签名中。

完整可运行示例见 `samples/OpcUaAndDevices`。
