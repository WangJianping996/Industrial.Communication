# API 文档与兼容性

每个发布包都包含对应程序集的 XML 文档，IDE 安装 NuGet 包后可直接显示摘要、参数和返回值说明。Nullable 已启用，Release 构建将缺少公开 XML 文档的 `CS1591` 视为错误。

`api/Communication.*.txt` 是 `0.1.0` 的公开 API 基线。CI 在 Release 构建后运行：

```powershell
dotnet run --project tools/ApiSurface -- --verify
```

有意添加或修改公共 API 时，先评审二进制/源码兼容性、Nullable 语义和默认值，再更新基线：

```powershell
dotnet run --project tools/ApiSurface -- --write
```

预览版允许必要调整，但必须记录在 `CHANGELOG.md`。稳定版后，删除或改变公共签名需要主版本升级或经过弃用周期。
