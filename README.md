# jt（Windows 10 版）

`jt` 是一个单文件命令行工具：把 API Token、密码这类密钥 AES-GCM 加密后存在本机 vault 里，交给 AI 的只是一个引用 `jt://secret/<id>`。AI 需要用密钥时运行 `jt resolve <引用> --exec <命令>` 或 `jt env <命名空间> -- <命令>`，真值只注入到那个子进程的环境变量，不打印、不进对话、不进日志。vault 是一个 Git 仓库，推到你自己的私有 GitHub 仓库就能在多台机器之间同步。

这个版本只支持 Windows，源自 [catoncat/jt](https://github.com/catoncat/jt)（macOS / Linux），并把 [jiantieban](https://github.com/catoncat/jiantieban) 的"复制 → 标记为密钥 → 贴引用"流程搬了过来：命令行 `jt grab`，以及一个托盘图形界面 `jt-gui.exe`（见下文）。

## 解决的问题

把 Cloudflare 的 API Token 直接贴给 AI，会触发安全拦截，明文也会留在聊天记录和日志里。换成：

```powershell
# 1. 在 Cloudflare 后台复制 token，然后：
jt grab cf/CLOUDFLARE_API_TOKEN --description 'Cloudflare API token, zone DNS edit'
# grabbed cf/CLOUDFLARE_API_TOKEN jt://secret/Efgh5678
# reference copied to clipboard; the plaintext is no longer on it

# 2. 剪贴板里现在是 jt://secret/Efgh5678，粘贴给 AI：
#    "用 jt://secret/Efgh5678 这个 token 把 example.com 的 A 记录改成 1.2.3.4"

# 3. AI（装了 skills/jt-secret）会这样用它，真值只在子进程里：
jt resolve jt://secret/Efgh5678 --env CLOUDFLARE_API_TOKEN --exec wrangler whoami
jt env cf -- wrangler whoami
```

## 安装

要求：Windows 10 或 11，[Git for Windows](https://git-scm.com/download/win)（同步用），Windows PowerShell（`--clear-history` 用）。

从源码构建（需要 Go 1.26 或更高；GUI 用 Windows 自带的 C# 编译器，不用装别的）：

```powershell
.\build.ps1                                   # 产出 bin\jt.exe 和 bin\jt-gui.exe
New-Item -ItemType Directory -Force "$env:LOCALAPPDATA\Programs\jt" | Out-Null
Copy-Item bin\jt.exe, bin\jt-gui.exe "$env:LOCALAPPDATA\Programs\jt\"
# 把 %LOCALAPPDATA%\Programs\jt 加入用户 PATH；jt-gui.exe 可以固定到任务栏或开始菜单
```

发布包：推送 `v*` 标签后，release 工作流在 Windows runner 上跑测试并产出 `jt-windows-amd64.zip` / `jt-windows-arm64.zip`（各含 `jt.exe` 和 `jt-gui.exe`）和 sha256，见 [Releases](https://github.com/Aonggg/jt/releases)。一行安装：

```powershell
irm https://raw.githubusercontent.com/Aonggg/jt/main/install.ps1 | iex
```

安装脚本校验 sha256，安装到 `%LOCALAPPDATA%\Programs\jt\jt.exe` 并加入用户 PATH。环境变量 `JT_REPO`、`JT_VERSION`、`JT_INSTALL_DIR`、`JT_BASE_URL` 可以覆盖来源和位置。原仓库 catoncat/jt 的 release 只有 macOS / Linux 产物，不能用于这个版本。

## 初始化与同步

```powershell
gh repo create jt-vault --private          # 或在 GitHub 网页上建一个私有仓库
jt init --repo https://github.com/<你>/jt-vault.git
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
| `jt ref <name-or-ref>` / `jt ref <namespace>` | 把单条引用复制到剪贴板 / 把整组引用（`jt://env/<namespace>` + 每条名称和引用）复制到剪贴板 |
| `jt copy <name-or-ref>` | 把**明文**复制到剪贴板给你自己粘贴，标记为不进剪贴板历史 |
| `jt resolve <name-or-ref> [--env NAME] --exec CMD [ARGS...]` | 解密后以环境变量（默认 `JT_SECRET`）运行命令 |
| `jt env <namespace> -- CMD [ARGS...]` | `namespace/VAR` 下所有条目变成子进程的环境变量 `VAR`；也接受组引用 `jt://env/<namespace>` |
| `jt describe` / `jt mv` / `jt rm` | 改描述 / 改名 / 删除 |
| `jt mv <namespace>/ <new>/` / `jt rm <namespace>/` | 重命名整组（引用不变）/ 删除整组；结尾的 `/` 是必需的 |
| `jt key export` / `jt key import [KEY]` | 主密钥经剪贴板在机器之间搬运 |
| `jt sync` / `jt status [--json]` | 拉取、提交、推送 vault / 查看本地是否有未同步改动 |

名称约定 `命名空间/VAR_NAME`，例如 `cf/CLOUDFLARE_API_TOKEN`、`cf/CLOUDFLARE_ACCOUNT_ID`。同一命名空间就是一个**组**：`jt env cf -- ...` 一次注入整组；`jt ref cf` 复制整组引用——第一行是组引用 `jt://env/cf`，后面每行一条 `名称  引用`，贴给 AI 它既能整组用（`jt env cf`），也能挑其中一条用：

```
jt://env/cf  （整组注入：jt env cf -- <命令>）
cf/CLOUDFLARE_ACCOUNT_ID  jt://secret/Abcd1234
cf/CLOUDFLARE_API_TOKEN  jt://secret/Efgh5678
cf/AWS_ACCESS_KEY_ID  jt://secret/Ijkl9012
cf/AWS_SECRET_ACCESS_KEY  jt://secret/Mnop3456
```

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

## 图形界面 jt-gui

`jt-gui.exe` 是 jiantieban 密钥视图的 Windows 版：常驻托盘，**Ctrl+Shift+J** 呼出 / 收起面板，**Ctrl+Shift+G** 直接弹出"抓取剪贴板"。它只是 `jt` 的界面，所有操作都转成 `jt` 命令，自己不碰密钥文件；需要 `jt.exe` 在同一目录、`%LOCALAPPDATA%\Programs\jt`、PATH 或环境变量 `JT_BIN` 指向的位置。

面板里：

- 顶部搜索框直接打字过滤名称 / 描述 / ID（输入法在这个框里是关的，粘贴中文照样能搜）；↑↓ 选行，**Enter 复制引用并收起面板**，然后去 AI 对话里粘贴。
- 列表按命名空间分组显示，组头写着整组引用 `jt://env/cf` 和注入命令。**点组头选中整组**（右键组头直接出整组菜单，双击组头复制整组并收起面板）；Ctrl/Shift 多选几行再按 Enter，就复制选中的那几条。工具栏：抓取剪贴板、新建（手动输入，值用密码框）、复制引用（Enter）、复制整组（Ctrl+Enter）、复制明文（不进剪贴板历史）、改名（F2）、描述、查看/更新值、删除（选中多条就删多条）、同步、刷新（F5）。右键行也有这些。
- 组的管理：组就是命名空间，有一条就存在，一条不剩就消失。工具栏"新建组…"输入组名并存入第一条；右键组头 → "重命名组…"（组里每条改成 `新名/原名`，`jt://secret/…` 引用全部不变，只有 `jt://env/旧名` 变成新名）或"删除整组…"；选中整组后按 F2 也是重命名组。命令行对应 `jt mv cf/ cloudflare/` 和 `jt rm cf/`——必须带结尾的 `/`，防止把整组误当成一条。往已有组里加条目就是普通的新建/抓取，名称写成 `组名/NAME`。
- 查看/更新值：对话框里直接显示当前值（默认遮住，勾"显示"看明文；多行的值直接显示），改完确定就更新，引用不变。GUI 拿到这个值的方式和 Agent 一样——`jt resolve <引用> --exec jt-gui.exe --print-env JT_SECRET`，由子进程把环境变量写回来；`jt` 本身仍然不打印任何真值。
- 状态栏显示条数和同步状态；同步失败会弹出 git 的原话。
- 托盘菜单：显示 / 隐藏、抓取剪贴板、同步、开机自动启动到托盘、退出。关窗口只是收到托盘。

### 一整块账号信息自动拆开

复制下面这样的文本再按 Ctrl+Shift+G：

```
Cloudflare 账号 someone@example.com
帐户 ID是0d43…
API 令牌是cfat_…
访问密钥 ID是81ea…
秘密访问密钥是9162…
```

会弹出"拆成多条密钥"对话框：命名空间 `cf`，四条分别命名为 `CLOUDFLARE_ACCOUNT_ID`、`CLOUDFLARE_API_TOKEN`、`AWS_ACCESS_KEY_ID`、`AWS_SECRET_ACCESS_KEY`（R2 的 S3 兼容名），邮箱作为 `CLOUDFLARE_EMAIL`。双击可改名、取消勾选可跳过，也可以"整块存为一条"。确认后逐条存入，**整组引用**放进剪贴板，直接贴给 AI 就行。

识别分两层：

- **本地**：每行按 `标签: 值`、`标签=值`、`标签是值` 切开，右边整段算值；没有分隔符的行里把邮箱和每个"不是已知词汇的 ASCII 片段"挑出来。名称由标签翻译而来：中文走一张领域词典（约 150 个词：数据库、连接串、商户号、访问密钥、企业微信、机器人……）做最长匹配切词，英文按大小写和分隔符拆词，再套上工具实际读取的拼法——`数据库连接串 → DATABASE_URL`、`商户号 → MERCHANT_ID`、`企业微信机器人Webhook地址 → WECOM_BOT_WEBHOOK_URL`、`阿里云AccessKey ID → ALIBABA_CLOUD_ACCESS_KEY_ID`、`Secret Access Key → AWS_SECRET_ACCESS_KEY`。命名空间取文本里最先出现的品牌（cloudflare → `cf`、飞书 → `feishu`、阿里云 → `aliyun`）。词典之外的标签得到 `FIELD`，你在对话框里改。不联网，默认就是这样。
- **AI**（可选，工具栏或托盘菜单 → AI 设置）：接入 DeepSeek 或任何 OpenAI 兼容接口，让模型来起名、定命名空间、写描述——词典管不到的标签、散乱的格式、"这组是 R2 的"这类上下文判断，是它比词典强的地方。对话框先把**将要发出去的内容**原样显示出来，点"用 AI 命名"才发（也可以在设置里改成自动发）。API Key 本身存在 jt 里（默认名 `ai/DEEPSEEK_API_KEY`），不进注册表、不进 Git 明文。

发给模型的内容是**默认拒绝**生成的：只保留中文等非 ASCII 文字、一份标签/品牌词表里的词（api、token、账户、cloudflare、github……）、不超过 3 个字符的短片段和标点；其它所有 ASCII 片段、邮箱、`标签: 值` 的整个右边都换成 `<V1>`、`<EMAIL_1>` 这类占位符，另附"值的形状"（长度、字符种类、已知的厂商前缀如 `cfat_`、`ghp_`）。发送前程序再用同一套规则校验一遍出站文本，不合格就不发。所以能保证的是**真值不会出本机**，而不是"识别一定正确"——识别错了只会让名字起得不好，你在对话框里改。还剩两种它管不到的情况：一行里没有任何分隔符、而且整个秘密只由中文或词表里的词组成（例如一行只写"我爱北京天安门"），这种只能靠你看一眼预览；一行里塞了两个值（`AK: xxx SK: yyy`）会被当成一个值整体遮住，不泄露但要你拆成两行。

没有剪贴板历史管理——Windows 自己有 Win+V。

## 给 AI 用

1. 把 `skills/jt-secret/` 复制到你所用 Agent 的 skills 目录，每个 Agent 一份：
   - Claude Code：`%USERPROFILE%\.claude\skills\jt-secret\SKILL.md`（项目级放 `.claude\skills\`）
   - omp：`%USERPROFILE%\.agents\skills\jt-secret\SKILL.md`
   - pi：`%USERPROFILE%\.pi\agent\skills\jt-secret\SKILL.md`
   - WSL 里的 Agent：对应的 Linux 家目录，如 `~/.agents/skills/jt-secret/`、`~/.claude/skills/jt-secret/`
2. 对话里只贴引用。`jt ls` 或 `jt ref <name>` 随时能拿到引用；贴整组就 `jt ref cf`。
3. Agent 看到 `jt://secret/...` 或 `jt://env/...` 时会用 `jt resolve ... --exec` / `jt env` 消费真值，并按 skill 里的规则不打印、不落盘。
4. 给 AI 的 token 用**最小权限**：Cloudflare 后台为这件事单独建一个 token，只限定需要的 zone 和权限，用完可以直接吊销。

### 一台电脑上的所有终端

引用是机器级的，不属于某个 AI：任何能运行 `jt` 的终端都能用同一份 vault——Claude Code、omp、pi，PowerShell、cmd、Git Bash 里的都一样，因为 `jt.exe` 在用户 PATH 上，密钥和 vault 在 `%LOCALAPPDATA%\jt`。没装 skill 的 Agent 看到剪贴板里那段"整组注入：jt env cf -- <命令>"也知道怎么做，只是不如装了 skill 稳。

**WSL 是另一个操作系统**，Windows 版 `jt.exe` 在 WSL 里虽然能调用，但它启动的是 Windows 进程，注入不到 Linux 命令里。所以 WSL 里要装 Linux 版的 `jt`，共用同一个 vault：

```bash
# 在 WSL 里（以 release 的 amd64 包为例）
curl -fsSL https://github.com/Aonggg/jt/releases/latest/download/jt-linux-amd64.tar.gz | tar -xz -C /usr/local/bin jt
jt key import                 # 先在 Windows 侧 jt key export，这里直接读 Windows 剪贴板
jt init --vault /mnt/c/Users/<你>/AppData/Local/jt/vault   # 共用 Windows 那份 vault（同一个 git 仓库）
jt ls
jt env cf -- bash -c 'echo ${#CLOUDFLARE_API_TOKEN}'       # 注入的是 Linux 进程
```

Linux 版和 Windows 版命令一致：`grab`/`ref`/`copy`/`key export` 读写的是 Windows 剪贴板（通过 `powershell.exe`），`--clear-history` 也能清 Win+V。密钥文件是原版 jt 的 32 字节裸格式，存在 `~/.config/jt/key`；`jt init` 发现 vault 里已有条目时不会另造密钥，而是提示你导入。不想共用目录也可以在 WSL 里 `jt init --repo <私有仓库>` 单独 clone，靠 `jt sync` 同步。

## 边界，别高估

- jt 防的是**顺手泄露**：明文不进对话、不进工具输出、不进 Git。它不防蓄意——AI 以你的用户身份跑命令，`jt copy` 加读剪贴板、或者直接读 `key` 和 `vault.json`，都能拿到真值。真正的边界是你给那个 token 的权限。
- Windows 剪贴板历史（Win+V）若已开启，你在浏览器里复制 token 的那一刻它就进了历史。`jt grab --clear-history` 会清空历史（保留固定项）；不加这个参数时 jt 只提醒。jt 自己写入的明文（`copy`、`key export`）都带"不进历史、不同步到云"的标记。
- 描述是明文元数据并进 Git 历史，只写用途、归属这类说明，不写密钥。
- 第一条非空描述会把 vault 升到 v2；用同一个仓库的其它客户端（包括 Mac 上的原版 jt）要先升级到支持 v2 的版本。

## 与原版 jt 的差异

- Windows 构建：默认目录 `%LOCALAPPDATA%\jt`（原版 `~/.config/jt`），主密钥 DPAPI 保护；Linux 构建保留原版布局和裸密钥文件，面向 WSL，读写 Windows 剪贴板走 `powershell.exe` 互操作。
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
.\build.ps1          # bin\jt.exe（Go）+ bin\jt-gui.exe（C#，用 Windows 自带的 csc 编译）
```

`gui\` 是 C# 5 代码：Windows 自带的 .NET Framework 编译器只认到这个版本，所以没有字符串插值、`?.` 之类的语法。图标用 `gui\make-icon.ps1` 重新生成。测试使用临时目录、假剪贴板和本地 bare 仓库，不碰你的真实 vault 和剪贴板。推送 `v*` 标签会触发 release 工作流，产出 `jt-windows-amd64.zip` / `jt-windows-arm64.zip`（各含两个 exe）和 sha256。

## License

MIT，见 [LICENSE](LICENSE)。
