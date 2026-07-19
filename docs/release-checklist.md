# 0.1.0-preview.1 发布检查清单

## 已完成的本地门禁

- [x] 9 个发布包统一使用语义版本 `0.1.0-preview.1`，目标资产仅为 `netstandard2.1`。
- [x] 包 ID、标题、描述、标签、作者、MIT SPDX 许可证和 release notes 已配置。
- [x] 每个包包含 README、LICENSE、CHANGELOG、128×128 PNG 图标和 XML 文档。
- [x] Release 确定性构建、Portable PDB、`.snupkg` 和 CI 环境 Source Link 已配置。
- [x] Nullable 已启用，公开成员缺少 XML 文档会触发 `CS1591` 构建错误。
- [x] 公开 API 快照已生成，CI 会拒绝未评审的签名变化。
- [x] 单元、协议、集成、并发、重连风暴、加速长稳和失败注入测试已纳入自动化。
- [x] 包审计检查框架资产、元数据、符号、源码、测试目录、证书/私钥和私钥文本头。
- [x] 包依赖按 Abstractions → Core → Transports → 协议/Adapters 分层；OPC UA SDK 只位于独立包。
- [x] 空白 `net10.0` 项目只从本地 NuGet 源安装包并运行，不使用源码项目引用。
- [x] CI 配置 restore、Release build、test、API verify、pack、package audit 和 artifact upload。
- [x] 普通分支生成 `0.1.0-preview.<run>` 产物；`v*` Tag 使用 Tag 版本并进入 NuGet.org 发布步骤。
- [x] README、快速开始、协议文档、故障排查、安全、支持矩阵、API 说明和 Backlog 已发布。

## 正式推送前必须由发布者完成

- [ ] 将目录置于真实 Git 仓库中并确认远端地址；本地目录当前没有 `.git`，不能伪造 RepositoryUrl 或 Source Link commit。
- [ ] 使用正确地址打包，并要求包审计匹配：

  ```powershell
  $repositoryUrl = 'https://github.com/OWNER/REPOSITORY'
  dotnet pack Communication.sln -c Release -o artifacts/packages `
    /p:RepositoryUrl=$repositoryUrl
  ./eng/verify-packages.ps1 -PackageDirectory artifacts/packages `
    -ExpectedRepositoryUrl $repositoryUrl
  ```

- [ ] 在 GitHub 仓库 Secret 中配置 `NUGET_API_KEY`；禁止写入源码、脚本参数历史或日志。
- [ ] 在 Windows/Linux 真实或虚拟串口、目标 PLC 和目标 OPC UA 服务器完成互操作验收。
- [ ] 确认包 ID 在 NuGet.org 的所有权、组织与发布权限。
- [ ] 创建 `v0.1.0-preview.1` Tag，通过受保护发布环境执行首次推送。
- [ ] 从 NuGet.org 再次安装全部已发布包，并核对 README、图标、依赖、符号和 Source Link。
- [ ] 收集预览反馈后再决定稳定版版本号；不得直接把未经现场验证的预览标为稳定版。

外部发布、创建 Tag、写入 Secret 和推送 NuGet 均会改变外部状态，本地发布演练不会代替这些人工授权步骤。
