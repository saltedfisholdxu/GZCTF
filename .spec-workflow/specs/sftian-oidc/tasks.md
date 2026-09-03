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
- [x] 删除 CI 中重复的单元测试、集成测试、测试容器和前端检查，仅保留生产镜像构建发布与手工部署。
- [ ] 备份并隔离验收后再执行生产 Keycloak 配置、用户迁移和部署。
