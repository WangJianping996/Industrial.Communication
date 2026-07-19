# 0.1.0 发布检查清单

## 构建与包质量

- [x] 9 个发布包统一使用语义版本 `0.1.0`，目标资产仅为 `netstandard2.1`。
- [x] 包 ID、标题、描述、标签、作者、MIT 许可证和正式版发布说明已配置。
- [x] 包内 README 仅包含产品能力、安装方式、文档入口和使用示例。
- [x] 每个包包含 README、LICENSE、CHANGELOG、图标、XML 文档和符号包。
- [x] Release 构建、自动化测试、公开 API 基线和包内容审计已纳入 CI。
- [x] 包依赖保持 Abstractions → Core → Transports → 协议/Adapters 分层。
- [x] OPC UA SDK 只位于独立协议包，不进入公共通用接口。

## NuGet.org 发布

- [x] GitHub 仓库地址、Source Link 和包项目地址已经校验。
- [x] NuGet.org Trusted Publishing 策略已绑定 `WangJianping996/Industrial.Communication` 的 `ci.yml`。
- [x] GitHub Actions 发布作业具有 `id-token: write` 权限并使用短期凭据。
- [x] 9 个包 ID 均归 NuGet.org 用户 `WJP` 管理。
- [ ] 创建并推送 `v0.1.0` 标签。
- [ ] 确认 GitHub Actions 的构建、测试、打包和发布作业全部成功。
- [ ] 从 NuGet.org 公共源核对 9 个 `0.1.0` 包的 README、图标、依赖和符号。

发布后的功能边界和生产注意事项以 [支持矩阵](supported-features.md)、[安全说明](security.md) 和各协议指南为准。
