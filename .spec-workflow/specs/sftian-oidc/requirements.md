# SfTian OIDC 接入需求

## 目标

在 GZCTF v1.8.7 中接入 SfTian Keycloak 26.7.3，使用 OIDC Authorization Code + PKCE S256 完成登录，并继续由 GZCTF Identity Cookie 承担站内鉴权。

## 功能需求

1. `Sso.Enabled=false` 时不注册 OIDC 方案，页面不展示 SSO 入口，官方本地认证行为保持不变。
2. `Sso.Enabled=true` 时提供 `keycloak` OIDC 方案，固定回调路径为 `/api/sso/callback`，Scope 为 `openid profile email`。
3. SSO 登录完成后必须签发原有 `GZCTF_Token`，不得以 Keycloak Token 直接访问业务接口。
4. 外部账号按 `AspNetUserLogins(keycloak, sub)`、`gzctf_uid`、唯一已验证邮箱、新建用户的顺序关联。
5. 绑定后只认 `sub`；不覆盖已有用户名、邮箱或角色；本地 `Banned` 用户不得登录；外部角色不得提升本地权限。
6. 新建本地用户使用普通用户角色且 `EmailConfirmed=true`，不得新增用户字段或 EF migration。
7. 登录 return URL 仅允许本站相对路径，任何无效地址回退到 `/`。
8. 本地登出同时结束 Keycloak 会话；back-channel logout 必须验证 logout token 并使对应本地 Cookie 会话失效。
9. 本地密码登录与本地凭据管理分别可关闭；关闭后前端隐藏入口且后端返回 403。
10. 客户端 secret、授权码和 Token 不得进入源码、提交、响应、异常或日志。
11. 支持可选的 `display_name` claim，保留展示名称大小写。仅全新本地用户创建时优先使用该值，缺失或空白时回退到 `preferred_username`；显示名称不得参与账号匹配或角色判定。
12. Keycloak `display_name` 用户属性允许用户查看、编辑，但不设为必填，避免既有用户被强制补资料。已有绑定、迁移 GUID 或邮箱匹配到的本地用户均不得因该属性而改名。

## 用户迁移需求

1. 迁移源是生产 ReviewCTF 使用的 GZCTF `AspNetUsers`，迁移器使用 `--source gzctf`。
2. 保留本地 4,654 个用户、GUID、密码哈希及业务数据；使用既有 sf-identity migrator 将密码哈希导入 Keycloak。
3. 邮箱确认状态原样迁移：4,445 个已确认用户可直接登录，209 个未确认用户需先验证邮箱。
4. 新注册只在 Keycloak 进行；首次进入 GZCTF 时创建本地业务档案。
5. 不修改 sf-identity、CTF-Platform 或 Start_your_linux，不由 GZCTF 调用 Keycloak Admin API。

## 验收标准

- 新用户首次登录仅创建一个普通本地用户；老用户落到原 GUID；重复登录不创建第二行。
- 队伍、提交和成绩继续关联原用户；封禁与管理员权限仍完全由本地角色决定。
- 篡改 claim、错误 secret、错误 redirect URI、无效 logout token 均不能建立或保留会话。
- SSO 前后通道登出有效，回调由后端处理且不会落入 SPA fallback。
- SSO 关闭后可回退到官方本地认证流程。
