# SfTian OIDC 部署、迁移与回滚

本文适用于从官方 GZCTF `v1.8.7`（`932b0d3a7e98e79dac88cede6657d17ab271b5d1`）派生的 SfTian 版本，并对接 Keycloak 社区版 26.7.3。GZCTF 继续使用 ASP.NET Core Identity Application Cookie（`GZCTF_Token`）做站内鉴权，Keycloak Token 不直接进入业务接口。

## Keycloak client

Realm：`sftian`；Client ID：`gzctf`；客户端类型：Confidential。

必须配置：

- Valid redirect URI：`https://gz.imxbt.cn/api/sso/callback`
- Valid post logout redirect URI：`https://gz.imxbt.cn/api/sso/signed-out`
- Backchannel logout URL：`https://gz.imxbt.cn/api/sso/backchannel-logout`
- Web Origin：`https://gz.imxbt.cn`
- Standard Flow：启用
- PKCE method：`S256`
- Implicit Flow：禁用
- Direct Access Grants：禁用
- Scope：`openid profile email`

为 `gzctf` client 创建 User Attribute mapper，把 `sf_identity_source_id` 输出为 claim `gzctf_uid`，并勾选 ID Token、Access Token、UserInfo。Realm User Profile 中的 `sf_identity_source` 和 `sf_identity_source_id` 必须只允许管理员编辑，普通用户不得写入或修改。

不得把 `realm-management`、realm 管理员或 master realm 角色映射为 GZCTF `Admin`。站内权限只认本地 `AspNetUsers.Role`。

### 可选显示名称

Keycloak 本地登录用户名会统一为小写。需要保留展示名称大小写时，在 `sftian → Realm settings → User profile` 新增独立属性：

- Name：`display_name`；Display name：`显示名称 / Display name`。
- Required：关闭；Multivalued：关闭；用户和管理员均可查看、编辑。
- `length` validator：min `3`、max `15`；不要对该属性做小写转换。
- 不批量修改老用户，不改变 `username`、邮箱验证状态、迁移来源属性或本地角色。

在 `Clients → gzctf → Client scopes → gzctf-dedicated → Mappers` 新建 User Attribute mapper：User Attribute 和 Token Claim Name 均为 `display_name`，Claim JSON Type 为 `String`，开启 ID Token、Access Token、UserInfo。

该字段在 Keycloak 中只是可编辑的展示资料，不要求唯一，也不用于登录或账号链接。GZCTF 仅在首次创建全新本地用户时按 `display_name → preferred_username → 邮箱前缀` 初始化本地用户名，保留大小写并沿用长度限制；重名时追加稳定后缀，绝不绑定到同名账号。已有本地档案保持原用户名，后续修改 Keycloak 显示名称也不会自动改名。缺少此字段或 mapper 时仍保持原流程，无新增环境变量或数据库迁移。

## GZCTF 运行配置

所有值由 `gzctf-server` namespace 中的 Deployment/Secret 注入；不要写入 Git、ConfigMap、日志或 `sf-identity/.env`：

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

最终切换时的生产非密钥值：

```text
GZCTF_Sso__Enabled=true
GZCTF_Sso__Authority=https://sso.imxbt.cn/realms/sftian
GZCTF_Sso__ClientId=gzctf
GZCTF_Sso__LocalAuthenticationEnabled=true
GZCTF_Sso__LocalCredentialManagementEnabled=false
GZCTF_AccountPolicy__AllowRegister=false
GZCTF_ConnectionStrings__RedisCache=gzctf-garnet:6379
```

发布准备阶段先应用 `deploy/kubernetes/gzctf-sso-config.yaml`，其中固定使用 `Sso.Enabled=false`、`LocalAuthenticationEnabled=true`、`LocalCredentialManagementEnabled=true`、`AllowRegister=true`。这一步只创建配置对象，不切换认证；维护窗口前不得提前套用上述最终切换值。

`GZCTF_Sso__ClientSecret` 只从 Keycloak `Clients → gzctf → Credentials` 复制到独立 Kubernetes Secret。另在 `gzctf-server` 创建阿里云 ACR image pull Secret，并在 Deployment 的 `imagePullSecrets` 引用它。仓库 Actions Secret 名称为 `ALIYUN_CR_USERNAME`、`ALIYUN_CR_PASSWORD`、`KUBECONFIG` 和构建所需的 `SIXLABORS_LICENSE`。

反向代理必须传递 `X-Forwarded-Proto: https`。GZCTF 的 `ForwardedHeaders`、`KnownProxies` 或 `KnownIPNetworks` 必须只信任实际代理；上线前确认 Challenge 生成的 redirect URI 是 `https://gz.imxbt.cn/api/sso/callback`。

## 镜像与 AGPL 源码

Gitea Actions 在 `main` 通过验证后构建 linux/amd64 镜像：

```text
registry.cn-hangzhou.aliyuncs.com/sf_project/gzctf:prod-<sha8>
```

生产只拉取该内部 ACR 镜像，不覆盖官方镜像。每个镜像标签的 `<sha8>` 必须对应：

- 私有交付仓库：`https://gitea.imxbt.cn/SfTian/GZCTF`
- AGPL 公开源码仓库：`https://github.com/saltedfisholdxu/GZCTF`

公开仓库与私有仓库必须推送同一个完整 commit SHA；发布说明应同时列出镜像标签、完整 SHA 和公开源码链接。不得把部署 Secret、数据库备份或生产配置提交到公开仓库。

公开发布时保留上游的 `LICENSE`、`NOTICE`、`LICENSE_ADDENDUM.txt` 及受限组件标识；本改造不删除、改名或重新授权上游受限组件。SfTian 对外提供实际部署修改版的对应源码 commit。

Gitea Runner 已配置海外访问代理，CI 直接下载 Actions、NuGet、pnpm 依赖和官方基础镜像；仓库不提交依赖缓存或第三方二进制包。

后续跟随官方发版时，以新的官方 tag 为基线 rebase 本分支，解决冲突后重新运行全部测试并重建镜像；不要复用旧 tag 的二进制产物。

## 迁移门禁与步骤

生产 ReviewCTF 使用 GZCTF 数据结构，迁移器固定使用 `--source gzctf`。2026-09-03 发布前复核为 4,655 个用户：4,446 个邮箱已确认，209 个邮箱未确认，缺失邮箱、缺失密码哈希和规范化邮箱重复数均为 0。用户数据会继续变化，迁移窗口必须重新执行 preflight，不能把该快照当成最终计数。

开始前必须分别对 GZCTF PostgreSQL 和 Keycloak PostgreSQL 执行 `pg_dump -Fc`，记录 SHA-256，并在隔离数据库实际恢复验证。备份文件不得进入仓库。

1. 在隔离恢复数据上执行现有 `sf-identity-migrator preflight/finalize/verify --source gzctf`。
2. 部署候选 GZCTF，但保持 `GZCTF_Sso__Enabled=false`，在隔离环境完成 OIDC 验收。
3. 维护窗口关闭本地注册、找回、重置和修改密码，冻结 GZCTF 密码哈希。
4. 对生产执行 `preflight --source gzctf`；任何 source id、邮箱或用户名冲突都立即停止。
5. 执行 `finalize --source gzctf` 和 `verify --source gzctf`，确认迁移窗口 preflight 得到的全部 source id 完整映射。
6. 开启 SSO，暂时保留 `LocalAuthenticationEnabled=true`；不要长期运行 `watch`。
7. 完成验收和 24 小时日志观察后，再关闭本地密码登录。

原 `AspNetUsers` 行、GUID、PasswordHash、队伍、提交和成绩不得删除或改主键。`AspNetUserLogins` 由应用首次登录时写入，无 EF migration。209 个未确认邮箱账号必须先在 Keycloak 完成邮箱验证。

## 验收

- 新用户首次 SSO 只新增一个 `Role.User`，`EmailConfirmed=true`，可访问队伍和题目流程。
- 老用户通过 `gzctf_uid` 落到原 GUID，队伍、提交和成绩保持。
- 同一 `sub` 重复或并发登录不新增用户；已经绑定后不再按邮箱识别。
- 本地 `Banned` 仍拒绝；Keycloak 管理角色不会提升 GZCTF 权限。
- 本地 SSO 登出结束 Keycloak 会话；back-channel logout 使旧应用 Cookie 失效。
- 篡改 JWT/`gzctf_uid`、错误 client secret、未登记 redirect URI 均失败。
- 日志中不存在 client secret、授权码、ID token、access token 或 refresh token。
- `Sso.Enabled=false` 时不注册 `keycloak` scheme，本地认证保持官方行为。
- 代理后的 callback 与 post logout redirect 都使用 HTTPS。

结果必须分别报告：源码已推送、镜像已发布、Pod Ready、迁移已验证、端到端登录已通过；不得用其中一项代替其他状态。

## 回滚

将 Deployment 镜像恢复为 `registry.cn-shanghai.aliyuncs.com/gztime/gzctf:v1.8.7`，并设置 `GZCTF_Sso__Enabled=false`，恢复本地认证策略。原 PasswordHash 从未删除，旧版会忽略残留的 `AspNetUserLogins` 绑定。

若在开放用户流量前发现迁移错误，停止切换并恢复 Keycloak 备份；已经开放新用户流量后不得直接覆盖数据库，应先评估新增身份和业务数据再制定合并或回滚方案。
