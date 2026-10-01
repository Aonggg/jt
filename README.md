# jt（Windows 10 版）

`jt` 是一个单文件命令行工具：把 API Token、密码这类密钥 AES-GCM 加密后存在本机 vault 里，交给 AI 的只是一个引用 `jt://secret/<id>`。AI 需要用密钥时运行 `jt resolve <引用> --exec <命令>` 或 `jt env <命名空间> -- <命令>`，真值只注入到那个子进程的环境变量，不打印、不进对话、不进日志。vault 是一个 Git 仓库，推到你自己的私有 GitHub 仓库就能在多台机器之间同步。

这个版本只支持 Windows，源自 [catoncat/jt](https://github.com/catoncat/jt)（macOS / Linux），并把 [jiantieban](https://github.com/catoncat/jiantieban) 的"复制 → 标记为密钥 → 贴引用"流程做成了命令行：复制密钥，运行 `jt grab`，剪贴板里的明文就变成了引用。

## 解决的问题

把 Cloudflare 的 API Token 直接贴给 AI，会触发安全拦截，明文也会留在聊天记录和日志里。换成：

```powershell
# 1. 在 Cloudflare 后台复制 token，然后：
jt grab cf/CLOUDFLARE_API_TOKEN --description 'Cloudflare API token, zone DNS edit'
# grabbed cf/CLOUDFLARE_API_TOKEN jt://secret/V245z3Ye
# reference copied to clipboard; the plaintext is no longer on it

# 2. 剪贴板里现在是 jt://secret/V245z3Ye，粘贴给 AI：
#    "用 jt://secret/V245z3Ye 这个 token 把 example.com 的 A 记录改成 1.2.3.4"

# 3. AI（装了 skills/jt-secret）会这样用它，真值只在子进程里：
jt resolve jt://secret/V245z3Ye --env CLOUDFLARE_API_TOKEN --exec wrangler whoami
jt env cf -- wrangler whoami
```

## 安装

要求：Windows 10 或 11，[Git for Windows](https://git-scm.com/download/win)（同步用），Windows PowerShell（`--clear-history` 用）。

从源码构建（需要 Go 1.26 或更高）：

```powershell
go build -trimpath -ldflags '-s -w' -o bin\jt.exe .\cmd\jt
New-Item -ItemType Directory -Force "$env:LOCALAPPDATA\Programs\jt" | Out-Null
Copy-Item bin\jt.exe "$env:LOCALAPPDATA\Programs\jt\jt.exe"
# 把 %LOCALAPPDATA%\Programs\jt 加入用户 PATH
```

发布包：把这个仓库推到你自己的 GitHub 账号并打一个 `v*` 标签，release 工作流会在 Windows runner 上跑测试并产出 `jt-windows-amd64.zip` / `jt-windows-arm64.zip` 和 sha256。然后：

```powershell
$env:JT_REPO = '<you>/jt'
irm https://raw.githubusercontent.com/<you>/jt/main/install.ps1 | iex
```

安装脚本校验 sha256，安装到 `%LOCALAPPDATA%\Programs\jt\jt.exe` 并加入用户 PATH。环境变量 `JT_REPO`、`JT_VERSION`、`JT_INSTALL_DIR`、`JT_BASE_URL` 可以覆盖来源和位置。原仓库 catoncat/jt 的 release 只有 macOS / Linux 产物，不能用于这个版本。

## 初始化与同步

```powershell
gh repo create jt-vault --private          # 或在 GitHub 网页上建一个私有仓库
jt init --repo https://github.com/<you>/jt-vault.git
jt sync
```

- 默认目录 `%LOCALAPPDATA%\jt`：`config.json`、`key`、`vault\vault.json`。`JT_HOME`、`JT_VAULT_DIR`、`JT_KEY_FILE` 可以改位置。
- 主密钥 `key` 用 Windows DPAPI 保护：只有这台机器上的这个 Windows 用户能解开；把文件拷到别处没用。它永远不进 Git。
- 多机同步：本机 `jt key export`（base64 密钥复制到剪贴板，不进剪贴板历史）→ 另一台机器 `jt init --repo ...` 之后 `jt key import`（读剪贴板）。给 macOS / Linux 上的原版 jt 用：`echo '<粘贴>' | base64 -d > ~/.config/jt/key`。
- vault 里只有密文、遮罩预览、名称、描述和时间戳；格式与原版 jt 的 v1 / v2 完全一致，同一个私有仓库可以同时给 Mac 上的 jt / jiantieban 用。
- `jt init` 不带 `--repo` 也能用（只在本机，不同步）。

## 命令

| 命令 | 作用 |
|---|---|
| `jt grab <name> [--description TEXT] [--clear-history]` | 加密剪贴板里的文本，把剪贴板换成 `jt://secret/<id>` 引用 |
| `jt add <name> [--from-clipboard] [--description TEXT]` | 从 stdin（或剪贴板）读值新建一条，不改剪贴板 |
| `jt set <name-or-ref> [--from-clipboard] [--description TEXT]` | 换掉某条的值，ID 和引用不变 |
| `jt ls [--json] [query]` | 列出名称、引用、遮罩预览、描述；永不输出真值或密文 |
| `jt ref <name-or-ref>` | 把引用复制到剪贴板 |
| `jt copy <name-or-ref>` | 把**明文**复制到剪贴板给你自己粘贴，标记为不进剪贴板历史 |
| `jt resolve <name-or-ref> [--env NAME] --exec CMD [ARGS...]` | 解密后以环境变量（默认 `JT_SECRET`）运行命令 |
| `jt env <namespace> -- CMD [ARGS...]` | `namespace/VAR` 下所有条目变成子进程的环境变量 `VAR` |
| `jt describe` / `jt mv` / `jt rm` | 改描述 / 改名 / 删除 |
| `jt key export` / `jt key import [KEY]` | 主密钥经剪贴板在机器之间搬运 |
| `jt sync` / `jt status [--json]` | 拉取、提交、推送 vault / 查看本地是否有未同步改动 |

名称约定 `命名空间/VAR_NAME`，例如 `cf/CLOUDFLARE_API_TOKEN`、`cf/CLOUDFLARE_ACCOUNT_ID`，这样 `jt env cf -- ...` 一次注入整组。

`resolve` 和 `env` **直接运行**你给的程序，不会偷偷启动 shell。需要 shell 语法时自己写明：

```powershell
# 工具自己读环境变量（wrangler、gh、aws 等）——最常见，不需要 shell
jt env cf -- wrangler whoami

# 需要在命令里引用变量：Git Bash
jt resolve cf/CLOUDFLARE_API_TOKEN --exec bash -c 'curl -s -H "Authorization: Bearer $JT_SECRET" https://api.cloudflare.com/client/v4/user/tokens/verify'
# cmd
jt resolve cf/CLOUDFLARE_API_TOKEN --exec cmd /C "curl -s -H \"Authorization: Bearer %JT_SECRET%\" https://api.cloudflare.com/client/v4/user/tokens/verify"
# PowerShell
jt resolve cf/CLOUDFLARE_API_TOKEN --exec powershell -NoProfile -Command "curl.exe -s -H \"Authorization: Bearer $env:JT_SECRET\" https://api.cloudflare.com/client/v4/user/tokens/verify"
```

## 给 AI 用

1. 把 `skills/jt-secret/` 复制到你所用 Agent 的 skills 目录。Claude Code：`%USERPROFILE%\.claude\skills\jt-secret\SKILL.md`（项目级放 `.claude\skills\`）。
2. 对话里只贴引用。`jt ls` 或 `jt ref <name>` 随时能拿到引用。
3. Agent 看到 `jt://secret/...` 时会用 `jt resolve ... --exec` / `jt env` 消费真值，并按 skill 里的规则不打印、不落盘。
4. 给 AI 的 token 用**最小权限**：Cloudflare 后台为这件事单独建一个 token，只限定需要的 zone 和权限，用完可以直接吊销。

## 边界，别高估

- jt 防的是**顺手泄露**：明文不进对话、不进工具输出、不进 Git。它不防蓄意——AI 以你的用户身份跑命令，`jt copy` 加读剪贴板、或者直接读 `key` 和 `vault.json`，都能拿到真值。真正的边界是你给那个 token 的权限。
- Windows 剪贴板历史（Win+V）若已开启，你在浏览器里复制 token 的那一刻它就进了历史。`jt grab --clear-history` 会清空历史（保留固定项）；不加这个参数时 jt 只提醒。jt 自己写入的明文（`copy`、`key export`）都带"不进历史、不同步到云"的标记。
- 描述是明文元数据并进 Git 历史，只写用途、归属这类说明，不写密钥。
- 第一条非空描述会把 vault 升到 v2；用同一个仓库的其它客户端（包括 Mac 上的原版 jt）要先升级到支持 v2 的版本。

## 与原版 jt 的差异

- 只构建 Windows；默认目录 `%LOCALAPPDATA%\jt`（原版 `~/.config/jt`）。
- 主密钥 DPAPI 保护，同时能读原版的 32 字节裸密钥文件；新增 `jt key export / import`。
- 新增 `grab`、`ref`、`copy`，对应 jiantieban 的标记为密钥、贴引用、贴明文。
- `resolve` 必须带 `--exec`，jt 不再向 stdout 打印任何真值。
- `--exec` 单个字符串不再隐式交给 `/bin/sh`；需要 shell 时写明 `cmd /C` 或 `bash -c`。
- `--from-clipboard` 直接读 Windows 剪贴板，不依赖 pbpaste / xclip。
- stdin 和剪贴板输入末尾的一个 `\r\n` 或 `\n` 会被去掉（`echo` 和 PowerShell 管道都会多带一个）。

脚本和界面如何消费元数据见 [docs/metadata-integration.md](docs/metadata-integration.md)。

## 开发

```powershell
go vet ./...
go test ./...
go build -o bin\jt.exe .\cmd\jt
```

测试使用临时目录、假剪贴板和本地 bare 仓库，不碰你的真实 vault 和剪贴板。推送 `v*` 标签会触发 release 工作流，产出 `jt-windows-amd64.zip` / `jt-windows-arm64.zip` 和 sha256。

## License

MIT，见 [LICENSE](LICENSE)。
