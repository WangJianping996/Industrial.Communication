# 可靠通信与可观测性

第三天能力位于 `Industrial.Communication.Core`，类库资产仍仅为 `netstandard2.1`。

## 请求、响应与重试

`ReliableCommunicationClient<TRequest,TResponse>` 组合 `ITransportChannel`、`IProtocolCodec` 和响应关联器：

- 协议没有事务 ID 时使用 `SingleRequestCorrelator`，同一客户端最多保留一个在途请求。
- 协议携带事务 ID 时使用 `DelegatingResponseCorrelator`，可并发发送并处理乱序响应。
- `CommunicationRequestOptions.Timeout` 控制单次尝试的响应超时；外部取消始终通过 `CancellationToken` 传播。
- `ExponentialBackoffRetryPolicy` 的 `MaxAttempts` 包含首次调用，并支持最大延时、倍率、抖动比例和自定义可重试错误判断。

```csharp
var correlator = new DelegatingResponseCorrelator<Request, Response>(
    request => request.TransactionId.ToString(),
    response => response.TransactionId.ToString(),
    maxInFlight: 64);

var retry = new ExponentialBackoffRetryPolicy(
    maxAttempts: 3,
    initialDelay: TimeSpan.FromMilliseconds(100),
    maxDelay: TimeSpan.FromSeconds(2));

await using var client = new ReliableCommunicationClient<Request, Response>(
    channel,
    codec,
    correlator,
    new CommunicationClientOptions
    {
        DefaultTimeout = TimeSpan.FromSeconds(2),
        ProtocolName = "example"
    },
    retryPolicy: retry);
```

## 自动重连与心跳

传入 `ExponentialBackoffReconnectPolicy` 后，通道进入 `Faulted` 会触发有上限的自动重连。调用客户端或 `AutomaticReconnectCoordinator` 的 `DisconnectAsync` 会设置主动断开标志，已等待的退避也不会再次连接。恢复成功后按注册顺序执行 `IConnectionRecoveryHandler`，用于恢复订阅和监视；任一恢复钩子失败时本次恢复不算成功，并继续受最大重连次数限制。

`HeartbeatService` 不占用通道接收循环。调用方提供协议专用的心跳请求、响应校验和交换委托；达到连续失败阈值时触发 `Failed` 事件。

## 实时报文与脱敏

`MessageMonitor` 为每个订阅者使用有界缓冲区，慢订阅者只丢弃自己的最旧观测，不阻塞主通信链路。可按时间、方向、通道、会话和协议过滤。

安全默认值是 `SuppressPayloadMessageRedactor`：不输出原始载荷并设置 `IsRedacted`。只有显式使用 `PassThroughMessageRedactor` 才保留完整载荷。`RuleBasedMessageRedactor` 可按协议、方向和解析地址匹配规则，然后掩码字节区间及指定的 `Metadata` 字段；`DenyMessageRecordingRedactor` 会完全禁止记录。

解析摘要应只包含明确允许记录的非敏感信息，不能把密码、令牌、证书私钥或连接字符串写入 `Summary`。

## 历史、导出与回放

`FileMessageStore` 使用 JSON Lines 文件，支持按 UTC 日期和文件大小滚动、总容量上限、保留期限与查询过滤。应给它分配专用目录；实现只清理该目录中由自身命名的 `messages-*.jsonl` 文件。

```csharp
await using var monitor = new MessageMonitor();
await using var store = new FileMessageStore(new FileMessageStoreOptions
{
    DirectoryPath = Path.Combine(appDataPath, "communication-history"),
    RollFileSizeBytes = 16 * 1024 * 1024,
    MaxTotalSizeBytes = 256 * 1024 * 1024,
    RetentionPeriod = TimeSpan.FromDays(7)
});
await using var writer = new MessageStoreWriter(monitor, store);
writer.Start();
```

`MessageStoreWriter` 在后台消费监控流；存储返回失败或抛出异常时通过 `PersistenceFailed` 报告，不影响发布者。`MessageExporter` 支持 CSV/JSON，默认不导出载荷，只有 `IncludePayload: true` 才写入 Base64。目标流由调用方拥有，导出结束后仍保持打开。

`MessageReplayService` 支持原始时间间隔及倍速、固定间隔、尽快回放，并可仅回放一个方向。回放返回定时消息流，不会自行写入真实设备；调用方必须显式选择发送目标并承担设备安全联锁。

## 当前边界

- 文件历史适合 MVP 和单进程实例；多进程共享、数据库索引和加密静态存储尚未实现。
- 重试可能重复执行已经到达设备但响应丢失的写操作。写命令应使用协议事务语义、幂等设计或由调用方禁用自动重试。
- 监控缓冲区满时优先保护通信主链路，因此允许丢失观测消息。
