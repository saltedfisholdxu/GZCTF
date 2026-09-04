# SfTian OIDC 实施任务

- [x] 注册 SSO 配置、OpenIdConnect 方案与临时外部 Cookie。
- [x] 实现独立 SSO 控制器、账号关联服务和安全审计日志。
- [x] 实现应用 Cookie 的 SSO 会话 claims 与分布式撤销检查。
- [x] 对本地登录、注册、找回、重置和改密端点增加服务端策略门。
- [x] 增加登录页 SSO 按钮、整页登出、管理员只读状态和多语言文案。
- [x] 添加单元/集成测试，覆盖关联顺序、封禁、并发、重定向及 logout token。
- [x] 增加 Gitea Actions、运维配置说明、迁移/回滚和 PR 说明。
- [x] 完成本地编译、测试、格式与敏感信息检查。
- [x] 升级存在高危漏洞的 .NET 与测试容器依赖，并完成 NuGet 漏洞扫描、完整测试、linux/amd64 发布和 Docker 镜像构建验证。
- [x] 本地安装 `libpcap` 并完成启用流量捕获的动态容器集成测试；CI 不重复运行测试。
- [x] 提交并将同一 commit 推送到私有 Gitea 与 AGPL 公开 GitHub 仓库。
- [x] 增加最小权限部署/运行 RBAC、SSO 配置对象、镜像拉取 Secret 接入及完整 PodTemplate 滚动回滚流程。
- [x] 删除 CI 中重复的 ACR/Kubernetes 预检，生产发布直接消费 `main` 已验证并推送的精确 SHA 镜像。
- [x] 修复 ASP.NET Core Handler 已消费 state 后又由协议验证器重复校验导致的 `IDX21329`，并补充伪造 state 拒绝测试。
- [x] 删除 CI 中重复的单元测试、集成测试、测试容器和前端检查，仅保留生产镜像构建发布与手工部署。
- [x] 应用端仅接受 RS256，并让 ID Token 与 back-channel logout 共用算法白名单。
- [x] 应用端与生产 Keycloak client 双侧强制 PAR，禁止自动降级。
- [x] 将首次关联顺序修正为已有 sub、gzctf_uid、唯一已验证邮箱、新建用户。
- [x] 增加算法降级、强制 PAR、迁移用户无邮箱关联的回归测试。
- [x] 备份、修改并回读验证生产 Keycloak client 的 RS256 与 PAR 配置。
- [ ] 备份并隔离验收后再执行生产 Keycloak 配置、用户迁移和部署。

## 显示名称与用户资料

- [x] 全新 SSO 用户优先使用 `display_name` 初始化本地用户名，保留现有关联顺序和旧用户资料。
- [x] 测试大小写保留、缺省回退、冲突后缀，以及已有绑定/GUID/邮箱命中不改名。
- [x] 在 Keycloak 保存显示名称属性和 client 专属 mapper，并读回核对 ID Token、Access Token、UserInfo 开关。
- [x] 经用户确认，将显示名称设为用户必填，名／姓设为管理员专用，保留姓名数据。
- [x] 先备份再通过 Admin API 回填 4,655 个空显示名称，并逐项核验结果。
- [x] 在 SSO 模式个人资料页使用“本站显示名称”与说明，补齐 11 种语言，并完成前端验证。
- [x] 完成本地限定范围编译、SSO 测试和格式检查。提交独立 PR，合并与部署按用户显式授权进行。

2026-09-04 本地编译和限定 SSO 测试通过（31/31），改动范围的格式检查与 `git diff --check` 通过。代码经 PR #6 合并，生产镜像 `prod-a49cb198` 已滚动就绪。随后经用户授权用 Edge 桌面控制保存 Keycloak 属性、mapper 与资料规则，并用 Admin API 读回确认。

回填时 4,654 个账号使用原 GZCTF 用户名，1 个原名去掉边界空白后不足 3 字符的历史账号使用其当前合法 SSO 用户名；所有更新只写空 `display_name`，未改用户名、GUID、密码、角色或 GZCTF 数据。修改前记录保存在服务器私有备份目录。回填后目标值不一致为 0、空显示名称为 0，再次只读预检目标数为 0。

真实 SSO 注册页只读核验：显示名称输入框存在且标签为“显示名称 *”，`firstName`、`lastName` 输入框均不存在。未提交注册或发送邮件；本次配置核验不代表新账号首次打题的端到端验收。

个人资料页文案修正已通过 `pnpm build`（包含严格 TypeScript 检查）、限定文件 Prettier、11 种语言 JSON/键集合检查以及 `git diff --check`。本次只改变该字段的标签和说明，不修改 `userName` 保存逻辑。

## P0 加固验证（2026-09-04）

- SSO 定向单元测试：26/26 通过。
- 完整单元测试：179/179 通过。
- `GZCTF.slnx`：构建通过，0 警告、0 错误。
- SSO 认证集成用例：通过；沙箱内首次运行因 WebApplicationFactory 无法使用本地回环端口超时，按既有测试要求在沙箱外复跑通过。
- 生产 Keycloak client 已显式固定 ID/Access Token `RS256` 并要求 PAR；非 PAR 请求返回 `invalid_request`，正常登录仍取得 `urn:` `request_uri`。
- 生产备份：`/root/pg-backups/keycloak-gzctf-p0-20260904T053303Z-076c64ca`，完整解码校验通过。
