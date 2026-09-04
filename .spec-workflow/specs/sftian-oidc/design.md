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

`sftian` realm 新增可选的 `display_name` 用户属性，用户和管理员均可查看、编辑，保留大小写；推荐长度为 3–15，与 GZCTF 初始用户名限制一致。`gzctf` client 专属 User Attribute mapper 将其输出为同名字符串 claim（ID Token、Access Token、UserInfo）。登录标识仍是 Keycloak 规范化的 `username`，账号绑定仍只认 `sub`；后续修改显示名称不会同步覆盖 GZCTF 已有用户名。
