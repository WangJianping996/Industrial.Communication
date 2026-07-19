# API 约定

- 所有公共 I/O 方法以 `Async` 结尾并接受末尾 `CancellationToken`。
- 时间量使用 `TimeSpan`，不暴露含糊的整数毫秒参数。
- 连接和释放操作必须幂等；重复断开不得抛出资源生命周期异常。
- `CommunicationResult<T>` 用于预期内的链路、协议与设备失败；参数编程错误仍可抛出标准参数异常。
- 取消必须保留 `OperationCanceledException` 语义，超时使用独立的 `Timeout` 错误码。
- 公共接口不得暴露厂商 SDK、`Socket`、`SerialPort` 或日志框架具体类型。
- 公开载荷使用只读内存；实现不得在异步操作完成前复用调用方仍可能访问的缓冲区。
- 事件在内部锁外触发；实现应记录事件处理器异常但不能让其破坏 I/O 循环。
- 默认不记录完整原始载荷。启用监控时必须先应用脱敏策略，并标记 `IsRedacted`。
