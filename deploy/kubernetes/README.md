# GZCTF Kubernetes 发布准备

本目录只保存非密钥清单。任何 Client Secret、ACR 密码、ServiceAccount Token 或 kubeconfig 都不得提交到仓库。

## 对象与边界

- `gitea-deployer-rbac.yaml`：Gitea Runner 只能 `get/patch` `gzctf-server/gzctf` Deployment；不能列举 Deployment、ReplicaSet 或 Pod，不能读取 Secret，不能操作 PostgreSQL、Garnet 或集群级资源。
- `gzctf-runtime-rbac.yaml`：GZCTF 运行时只可列出 namespace，并在 `gzctf-challenges` 创建/删除题目 Pod/Service、创建/更新 NetworkPolicy 和镜像拉取 Secret。
- `gzctf-sso-config.yaml`：创建 SSO ConfigMap 与空 Secret。初始状态固定 `Sso.Enabled=false`，本地登录、凭据管理和注册保持开启，因此应用清单后不会切换认证方式。
- `gzctf-deployment-patch.yaml`：由 CI 替换 `__GZCTF_IMAGE__`，一次性更新镜像、运行 ServiceAccount、配置引用和健康探针，触发一个 Deployment RollingUpdate。

旧的 `gzctf-sa -> cluster-admin` 绑定在候选镜像完成动态题目创建/销毁验收前不得删除。候选 Pod 使用 `gzctf-runtime`；发布前 CI 会保存原 Deployment 的完整 PodTemplate、strategy、`minReadySeconds`、`progressDeadlineSeconds` 和 `revisionHistoryLimit`，失败时用 strategic patch 精确恢复。

## 首次准备（不切镜像）

```bash
kubectl get namespace gzctf-server gzctf-challenges
kubectl apply -f deploy/kubernetes/gitea-deployer-rbac.yaml
kubectl apply -f deploy/kubernetes/gzctf-runtime-rbac.yaml
kubectl apply -f deploy/kubernetes/gzctf-sso-config.yaml
```

将已有的 `sf-identity/aliyun-cr` 复制到 `gzctf-server/aliyun-cr` 时，只在集群内传输 Secret，不打印内容：

```bash
kubectl -n sf-identity get secret aliyun-cr -o json \
  | jq 'del(.metadata.namespace,.metadata.resourceVersion,.metadata.uid,.metadata.creationTimestamp,.metadata.managedFields) | .metadata.namespace="gzctf-server"' \
  | kubectl apply -f -
```

此时只创建对象，不 patch Deployment，所以线上仍运行官方 `v1.8.7`，SSO 也不会启用。

## Gitea Secrets

从 `gitea-gzctf-deployer-token` 生成的 kubeconfig 存入 Gitea 仓库 Secret `KUBECONFIG`。它应指向 Runner 可达的 Kubernetes API Server，且权限必须通过以下命令验证：

```bash
kubectl auth can-i patch deployment/gzctf \
  --namespace gzctf-server \
  --as system:serviceaccount:gzctf-server:gitea-gzctf-deployer
kubectl auth can-i get secrets \
  --namespace gzctf-server \
  --as system:serviceaccount:gzctf-server:gitea-gzctf-deployer
```

第一条必须为 `yes`，第二条必须为 `no`。另需配置：

- `ALIYUN_CR_USERNAME`
- `ALIYUN_CR_PASSWORD`
- `KUBECONFIG`
- `SIXLABORS_LICENSE`

ACR 凭据和 Kubernetes RBAC 只在初次配置或变更时由运维在本地验证，不在每次 CI 或生产发布中重复执行预检。

## 写入 OIDC Client Secret

从 Keycloak `Clients -> gzctf -> Credentials` 复制真实值，只写入 `gzctf-sso-secret` 的 `GZCTF_Sso__ClientSecret` 键。推荐通过临时权限受限文件注入，避免出现在命令行参数和 Git 中：

```bash
secret_file="$(mktemp)"
chmod 600 "${secret_file}"
read -r -s gzctf_client_secret
printf '%s' "${gzctf_client_secret}" > "${secret_file}"
unset gzctf_client_secret
kubectl -n gzctf-server create secret generic gzctf-sso-secret \
  --from-file=GZCTF_Sso__ClientSecret="${secret_file}" \
  --dry-run=client -o yaml | kubectl apply -f -
rm -f "${secret_file}"
```

写入 secret 不等于启用 SSO。只有显式把 `GZCTF_Sso__Enabled` 改为 `true` 并滚动 Deployment 后，OIDC 才会生效。

## 发布与回滚

测试只在合并前本地执行，不在 CI 重复运行。`main` 源码 push 只生成一次发布文件和 Docker 镜像，随后将该镜像标记为 `prod-<sha8>` 并推送 ACR。生产发布只允许从 `main` 手工触发并直接消费该镜像，再以一次 strategic merge patch 更新完整 PodTemplate；`maxUnavailable=0`、`maxSurge=1`，且 startup/readiness/liveness 都通过 `3000/healthz` 判定。

发布失败、超时或实际镜像不一致时，CI 将上述发布前状态组成含 `$patch: replace` 的 strategic patch，恢复完整 PodTemplate 与策略。发布和恢复的 Ready 判定都只读取 `deployment/gzctf` 的 generation、replicas、updatedReplicas、readyReplicas、availableReplicas 和 unavailableReplicas，不使用 `rollout status/undo`。

这样 deployer 不需要 namespace 级的 Deployment/ReplicaSet/Pod 读权限，也不会因为回滚而只替换 image 字段。如果发布期间检测到 Deployment spec 被其他操作修改，CI 会拒绝覆盖并停止自动恢复。上线后还必须分别报告：镜像已发布、Deployment rollout 成功、Pod Ready、SSO 已启用、迁移已验证、端到端登录已通过；这些状态不能互相代替。
