# PR：接入 SfTian 统一身份登录

## 摘要

- 在官方 `v1.8.7` 基线上增加 Keycloak OIDC Authorization Code + PKCE S256。
- 保留 Identity Application Cookie `GZCTF_Token` 和本地 Role 鉴权。
- 使用现有 `AspNetUserLogins` 绑定 `keycloak/sub`，无数据库 schema 变更、无 EF migration。
- 增加前后通道登出、Garnet 会话撤销、本地认证策略开关和登录/管理员 UI。
- 补齐 11 种语言资源、自动测试与 Gitea Actions。
- 增加最小权限 Gitea deployer / GZCTF runtime RBAC、SSO ConfigMap/Secret 模板、ACR pull Secret 接入和带健康探针的单次滚动发布清单。

## Keycloak 运维配置

- Valid redirect URI：`https://gz.imxbt.cn/api/sso/callback`
- Valid post logout redirect URI：`https://gz.imxbt.cn/api/sso/signed-out`
- Backchannel logout URL：`https://gz.imxbt.cn/api/sso/backchannel-logout`
- Web Origin：`https://gz.imxbt.cn`
- Client：Confidential；Standard Flow 开启；PKCE S256；Implicit 与 Direct Access Grants 关闭。
- Client mapper：`sf_identity_source_id` → `gzctf_uid`，启用 ID Token、Access Token、UserInfo。

## 新增运行变量

```text
GZCTF_Sso__Enabled
GZCTF_Sso__Authority
GZCTF_Sso__ClientId
GZCTF_Sso__ClientSecret
GZCTF_Sso__LocalAuthenticationEnabled
GZCTF_Sso__LocalCredentialManagementEnabled
GZCTF_AccountPolicy__AllowRegister
GZCTF_ConnectionStrings__RedisCache
```

真实 Client Secret 只放 `gzctf-server` namespace 的独立 Kubernetes Secret，不进入 Git、ConfigMap、日志或 `sf-identity/.env`。

## 迁移与镜像

- 不修改 `AspNetUsers` 主键、PasswordHash 或业务关系；不新增 migration。
- 现有 Rust migrator 固定使用 `--source gzctf`；迁移前必须完成两套 PostgreSQL 的备份、SHA-256 和隔离恢复演练。
- `main` 构建 `registry.cn-hangzhou.aliyuncs.com/sf_project/gzctf:prod-<sha8>`；生产改拉内部 ACR 镜像。
- 生产发布以一次 strategic merge patch 更新镜像和完整 PodTemplate；失败按旧 revision 执行 `kubectl rollout undo`，不是再次 `set image`。
- AGPL 公开源码同步到 `https://github.com/saltedfisholdxu/GZCTF`，并与私有 Gitea/镜像使用相同 commit SHA。
- 保留上游 `LICENSE`、`NOTICE`、`LICENSE_ADDENDUM.txt` 和受限组件标识，不对上游受限组件重新授权。
- 跟随上游时需 rebase 到新的官方 tag，重新测试并构建镜像。

## 验证

- .NET 10.0.300 restore 成功；主项目本地 C# 编译为 0 警告、0 错误。
- SSO 单元测试 25/25 通过；全量单元测试 178/178 通过。
- 全量集成测试 170/170 通过，使用 Testcontainers、本机已有的 `postgres:alpine` 和基于 Docker Hub `busybox:latest` 的 echo 测试镜像。
- 本次全部 C# 文件通过限定范围的 `dotnet format --verify-no-changes`；官方基线 `DockerProvider.cs` 仍有 4 处既有空白格式告警，本 PR 未改该文件。
- `pnpm check` 和 `pnpm build` 通过；linux-x64 ReadyToRun publish 与 linux/amd64 Docker 镜像构建通过。
- 11 种语言的 `account.json`、`admin.json` 均可解析且 key 集合一致；`git diff --check` 与敏感信息扫描通过。
- OIDC logout token 测试覆盖可信签名、issuer、audience、`iat`、`jti`、`sid/sub`、`events`、nonce 禁止项和错误签名。

本地构建与 Gitea Actions 均只通过 `SixLaborsLicenseKey` 环境变量注入已授权许可证；许可证内容不进入 Git、命令输出或构建日志。Gitea 仓库 Secret 名称为 `SIXLABORS_LICENSE`。

依赖安全更新将 Microsoft ASP.NET Core / EF Core 补丁组升级到 `10.0.10`、`System.IdentityModel.Tokens.Jwt` 升级到 `8.19.2`、Testcontainers 升级到 `4.14.0`（传递依赖 `SSH.NET 2026.0.0`），修复 `System.Security.Cryptography.Xml 10.0.9` 的五个高危 GHSA 与 `SSH.NET 2025.1.0` 的 `GHSA-q939-rpr3-3284`。四个 .NET 项目的 NuGet 传递依赖漏洞扫描均未再发现当前已知漏洞。Gitea 验证作业安装 `libpcap-dev`，供启用流量捕获的动态容器集成测试使用。

Gitea 分支 Run 132/133 已通过后端、前端、ReadyToRun 与 linux/amd64 Docker 验证；Run 134 的 CodeQL 失败来自 GitHub 专用上传接口在 Gitea 返回 404，现已限制 CodeQL 只在 GitHub 执行。

尚未完成：合并 `main`、发布生产镜像、候选 Deployment rollout、生产备份/迁移以及真实 Keycloak 端到端登录。代码分支验证不代表这些状态。
