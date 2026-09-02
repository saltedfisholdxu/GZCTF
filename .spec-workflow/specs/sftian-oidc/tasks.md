# SfTian OIDC 实施任务

- [x] 注册 SSO 配置、OpenIdConnect 方案与临时外部 Cookie。
- [x] 实现独立 SSO 控制器、账号关联服务和安全审计日志。
- [x] 实现应用 Cookie 的 SSO 会话 claims 与分布式撤销检查。
- [x] 对本地登录、注册、找回、重置和改密端点增加服务端策略门。
- [x] 增加登录页 SSO 按钮、整页登出、管理员只读状态和多语言文案。
- [x] 添加单元/集成测试，覆盖关联顺序、封禁、并发、重定向及 logout token。
- [x] 增加 Gitea Actions、运维配置说明、迁移/回滚和 PR 说明。
- [x] 完成本地编译、测试、格式与敏感信息检查。
- [x] 提交并将同一 commit 推送到私有 Gitea 与 AGPL 公开 GitHub 仓库。
- [ ] 备份并隔离验收后再执行生产 Keycloak 配置、用户迁移和部署。
