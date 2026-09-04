# SfTian OIDC 接入设计

## 认证流

浏览器访问 `/api/sso/login` 后由 `keycloak` 方案发起 Challenge。OIDC middleware 在 `/api/sso/callback` 校验 code、PKCE、state、nonce、issuer、audience 和签名，并把结果暂存于 `IdentityConstants.ExternalScheme`。`/api/sso/complete` 完成账号关联后，使用 `SignInManager` 签发 `GZCTF_Token`。

ASP.NET Core OIDC Handler 先用受保护的 state 与 correlation Cookie 完成 state 校验，随后会把协议消息中的 state 替换为可选的业务 `userstate`。因此保持 `OpenIdConnectOptions` 默认的 `ProtocolValidator.RequireStateValidation=false`，不得在协议验证器中重复启用 state 校验；伪造、缺失或无法解密的 state 仍会在 Handler 层被拒绝。

应用 Cookie 只复制登出所需的 `id_token`，同时加入内部 `sftian:sub`、`sftian:sid`、`sftian:login_at` claims。access token、refresh token 和授权码不进入应用 Cookie或日志。

## 账号关联

1. 先按 `FindByLoginAsync("keycloak", sub)` 查找已有绑定。
2. 首次登录时尝试将 `gzctf_uid` 解析为 GUID，并通过 `UserManager.FindByIdAsync` 定位迁移用户。
3. 未命中时只允许使用 `email_verified=true` 的邮箱；按规范化邮箱查询最多两条，只有唯一命中才允许绑定。
4. 仍未命中时创建普通本地用户。初始用户名优先取非空白的 `display_name`，其次取 `preferred_username`，再沿用邮箱前缀回退；保留大小写，沿用长度限制和基于 `sub` 的稳定冲突后缀。显示名称只用于新建，绝不参与账号匹配或覆盖已有关联账号。
5. 找到本地用户后先拒绝 `Role.Banned`，再调用 `AddLoginAsync`。并发冲突时重新按外部登录查找，不猜测绑定目标。

## 登出与撤销

前端使用同源整页 POST 调用 `/api/sso/logout`。服务端读取应用 Cookie 中的 `id_token`，同时退出 Application/External Cookie，并由 OIDC handler 跳转 Keycloak `end_session_endpoint`，登出后只回到固定本站地址。

back-channel endpoint 使用 OIDC discovery signing keys 验证 logout token，要求 back-channel logout `events`、`jti` 以及 `sid` 或 `sub`。撤销记录写入 `IDistributedCache`：sid 精确撤销；sub 保存撤销时间，仅拒绝更早签发的 Cookie。请求中间件在授权前检查记录并清除失效 Cookie，记录 TTL 为七天。

## 配置与回退

`SsoConfig` 从 `GZCTF_Sso__*` 读取。`Enabled=false` 时不注册 OpenIdConnect handler，SSO 中间件为空操作。本地登录和凭据管理开关只在 SSO 启用时生效。

生产使用 `GZCTF_ConnectionStrings__RedisCache=gzctf-garnet:6379`。Client secret 只通过 Kubernetes Secret 注入，管理员页面只展示脱敏状态。

## 运维边界

Keycloak client 只启用 confidential Authorization Code flow 与 PKCE S256。`sf_identity_source_id -> gzctf_uid` mapper 只属于 gzctf client，来源属性仅管理员可编辑。用户迁移继续由既有 Rust migrator 执行，GZCTF 代码不包含迁移或 Admin API 能力。

`sftian` realm 的 `display_name` 用户属性允许用户和管理员查看、编辑，对用户必填、对管理员非必填，保留大小写，长度为 3–15。`firstName`、`lastName` 保留字段和历史值，仅管理员可查看、编辑；用户注册和资料页不再渲染姓名输入框。`gzctf` client 专属 User Attribute mapper 将显示名称输出为同名字符串 claim（ID Token、Access Token、UserInfo）。登录标识仍是 Keycloak 规范化的 `username`，账号绑定仍只认 `sub`；后续修改显示名称不会同步覆盖 GZCTF 已有用户名。

GZCTF 个人资料页复用独立 `useSso` 的开关，只对该输入框切换“本站显示名称”标签与字段说明；不全局替换用户名翻译键。说明使用 Mantine 输入组件的关联 description，置于输入框下方，关闭 SSO 时不显示。底层 `userName` 表单值和保存请求完全不变。

启用显示名称必填前，按用户授权通过 Keycloak Admin API 回填空属性。迁移账号按原 source GUID 读取 GZCTF 用户名以保留大小写、不带迁移消歧后缀；原名不满足长度校验时使用该账号当前合法 SSO 用户名。仅补空值、不覆盖自定义显示名称，保存修改前记录并逐项读回，不直接写 Keycloak 数据库，不改密码、身份来源或本地业务数据。
