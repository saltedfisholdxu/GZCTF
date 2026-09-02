# PR：接入 SfTian 统一身份登录

## 摘要

- 在官方 `v1.8.7` 基线上增加 Keycloak OIDC Authorization Code + PKCE S256。
- 保留 Identity Application Cookie `GZCTF_Token` 和本地 Role 鉴权。
- 使用现有 `AspNetUserLogins` 绑定 `keycloak/sub`，无数据库 schema 变更、无 EF migration。
- 增加前后通道登出、Garnet 会话撤销、本地认证策略开关和登录/管理员 UI。
- 补齐 11 种语言资源、自动测试与 Gitea Actions。

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
- AGPL 公开源码同步到 `https://github.com/saltedfisholdxu/GZCTF`，并与私有 Gitea/镜像使用相同 commit SHA。
- 保留上游 `LICENSE`、`NOTICE`、`LICENSE_ADDENDUM.txt` 和受限组件标识，不对上游受限组件重新授权。
- 跟随上游时需 rebase 到新的官方 tag，重新测试并构建镜像。

## 验证

- .NET 10.0.300 restore 成功；主项目本地 C# 编译为 0 警告、0 错误。
- SSO 单元测试 25/25 通过；全量单元测试 178/178 通过。
- Authentication 集成测试 9/9 通过，使用 Testcontainers 和本机已有的 `postgres:alpine`。
- 本次全部 C# 文件通过限定范围的 `dotnet format --verify-no-changes`；官方基线 `DockerProvider.cs` 仍有 4 处既有空白格式告警，本 PR 未改该文件。
- `pnpm check` 和 `pnpm build` 通过；沙箱内 GitHub contributors 与 Google Fonts 在线获取失败时使用已有资源继续完成构建。
- 11 种语言的 `account.json`、`admin.json` 均可解析且 key 集合一致；`git diff --check` 与敏感信息扫描通过。
- OIDC logout token 测试覆盖可信签名、issuer、audience、`iat`、`jti`、`sid/sub`、`events`、nonce 禁止项和错误签名。

本机没有注入 SixLabors 商业许可证，因此没有把规避许可证检查的设计时编译冒充 Release publish 或 Docker 验证。Gitea Actions 使用仓库 Secret `SIXLABORS_LICENSE` 完成正式 Release publish 与显式 `linux/amd64` Docker 构建。

现有上游依赖在 restore/build 时报告：Testcontainers 引入的 `SSH.NET 2025.1.0` 对应 `GHSA-q939-rpr3-3284`；既有 DataProtection/NPOI 依赖链上的 `System.Security.Cryptography.Xml 10.0.9` 对应 `GHSA-23rf-6693-g89p`、`GHSA-8q5v-6pqq-x66h`、`GHSA-cvvh-rhrc-wg4q`、`GHSA-g8r8-53c2-pm3f`、`GHSA-mmjf-rqrv-855v`。本 PR 没有新增或升级这两项依赖，仍需单独跟随上游处理。

尚未完成：Gitea CI 实际运行、镜像发布、Pod Ready、生产备份/迁移以及真实 Keycloak 端到端登录。代码合并不代表这些状态。
