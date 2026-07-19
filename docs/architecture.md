# 架构与公共契约

## 目标

首版采用“传输、协议、设备、业务调用”分层。公共 API 只使用 BCL 类型与本项目模型，协议编解码器可以在没有网络、串口或真实 PLC 的情况下独立执行和测试。

所有发布类库仅目标化 `netstandard2.1`。测试和示例使用 `net10.0` 作为可执行宿主以验证现代 .NET 消费场景，但不参与打包。

## 包和依赖方向

```text
Industrial.Communication.Abstractions
                  ↑
Industrial.Communication.Core
                  ↑
Industrial.Communication.Transports
       ↑                  ↑
各协议扩展包       Industrial.Communication.Adapters
       └──────────┬───────┘
                  ↑
Industrial.Communication.DependencyInjection
```

- `Communication.Abstractions`：接口、公共模型、异常和连接状态机，不引用传输实现或厂商 SDK。
- `Communication.Core`：承载有界队列、帧解码、校验、重试、重连、心跳、监控、历史、导出和回放。
- `Communication.Transports`：已承载 Serial、TCP Client/Server、UDP。
- `Communication.Protocols.*`：协议专属编解码和客户端适配；Modbus 包已实现 TCP/RTU 客户端与模拟从站，不同协议包不得互相引用。
- `Communication.Adapters`：组合通道和协议的设备适配器。
- `Communication.DependencyInjection`：可选聚合注册；用户始终可以直接构造对象。

依赖只能沿箭头向下引用，禁止核心包反向引用协议包，也禁止协议模型进入 `Abstractions`。

## 公共 API 设计

- 异步优先：I/O 契约使用 `ValueTask`、`IAsyncEnumerable<T>` 和 `CancellationToken`。
- 超时显式：单次请求通过 `CommunicationRequestOptions.Timeout` 覆盖客户端默认值。
- 结果可判断：预期通信失败使用 `CommunicationResult<T>` 与 `CommunicationErrorCode`；需要异常流时使用 `GetValueOrThrow()`。
- 解码增量化：`IProtocolCodec<TRequest,TResponse>` 与 `IFrameDecoder` 接收 `ReadOnlySequence<byte>`，返回已消费/已检查字节数，支持半包、粘包和多帧缓存。
- 内存所有权明确：发送参数使用 `ReadOnlyMemory<byte>`；接收流的调用方若需跨迭代持有数据，必须复制。
- 可观测性默认安全：报文记录要先经过 `IMessageRedactor`；后续实现默认不得持久化完整敏感载荷。
- 批量调用隔离失败：PLC 批量 API 返回逐项结果，单个地址失败不隐藏其他成功结果。

## 连接状态机

允许的主要路径为：

```text
Disconnected → Connecting → Connected → Reconnecting
                    │            │            │
                    ├────────────┴────────────┤
                    ↓                         ↓
              Disconnecting ─────────→ Disconnected
                    │
                    └───────────────→ Faulted
```

取消连接可以从 `Connecting` 回到 `Disconnected`；任何活动阶段的不可恢复错误可以进入 `Faulted`；`Faulted` 可由显式连接、重连或断开操作恢复。相同状态和未列出的跃迁会被拒绝。

`ConnectionStateMachine` 使用锁提交状态，再在锁外触发事件，避免事件处理器造成锁重入或阻塞其他状态读取。

## 错误边界

| 类别 | 错误码 | 异常 |
|---|---|---|
| 超时 | `Timeout` | `CommunicationTimeoutException` |
| 取消 | `Canceled` | `OperationCanceledException` |
| 连接失败/中断 | `ConnectionFailure` | `ConnectionException` |
| 报文或协议错误 | `ProtocolError` | `ProtocolException` |
| 校验失败 | `ChecksumFailure` | `ChecksumException` |
| 设备异常响应 | `DeviceError` | `DeviceException` |

禁止依赖异常消息做分支判断；调用方应使用错误码或具体异常类型。

## 命名与版本

- 程序集和命名空间：`Communication.*`
- NuGet 包 ID：`Industrial.Communication.*`
- 初始版本：`0.1.0`
- 版本规则：SemVer；预览期允许基于变更日志调整 API，`1.0.0` 后破坏性变更只进入主版本。
- 许可证：项目自身采用 MIT。

2026-07-19 通过 NuGet V3 API 检查时，拟用的九个 `Industrial.Communication.*` 包 ID 均未发现已发布包；这不构成名称预留，正式推送前必须再次检查。

## 架构守卫

契约测试扫描 `Communication.Abstractions` 的公开 API，禁止出现：

- `System.Net.Sockets` 类型；
- `System.IO.Ports` 类型；
- Siemens、Mitsubishi、OPC Foundation 或其他具体 PLC SDK 类型。

协议层实现应通过离线 codec 测试和 Golden Tests 验证，不需要启动传输通道。
