---
name: jt-secret
description: Consume and name jt secret references (`jt://secret/...`) on Windows without ever exposing the value. Use when a `jt://secret/...` token appears in a conversation, file, or tool result, when the user asks to name a jt secret, when a command needs an API token or password that the user has stored in jt, or when the user pastes a plaintext secret that should be moved into jt.
---

# jt 密钥引用

`jt://secret/<id>` 是 jt 的**密钥引用**：指向一条加密存储的记录，本身不含真值。真值从引用推不出来，只能由 `jt` 在本机解密，并且只会注入到它启动的子进程的环境变量里。`jt` 永远不把真值打印到 stdout：`jt resolve <引用>` 不带 `--exec` 直接报错。

名称和引用都能用：`jt resolve cf/CLOUDFLARE_API_TOKEN ...` 与 `jt resolve jt://secret/Abcd1234 ...` 等价。

## 消费引用

当对话、文件或工具结果里出现 `jt://secret/...`，且接下来的命令需要真值时：

1. 看目标程序怎么读凭证。
   - **程序自己读环境变量**（wrangler、gh、aws、psql 等）：用 `--env` 指定它要的变量名，或用 `jt env <命名空间>` 一次注入整组。不需要 shell。
     ```powershell
     jt resolve jt://secret/Abcd1234 --env CLOUDFLARE_API_TOKEN --exec wrangler whoami
     jt env cf -- wrangler pages deploy ./dist
     ```
   - **要把值写进命令行参数或请求头**（curl 之类）：必须显式起一个 shell，让 shell 在子进程里展开变量。`jt` 不会替你猜 shell。
     ```powershell
     # Git Bash（Claude Code 的 Bash 工具就是它）
     jt resolve jt://secret/Abcd1234 --exec bash -c 'curl -s -H "Authorization: Bearer $JT_SECRET" https://api.cloudflare.com/client/v4/user/tokens/verify'
     # cmd
     jt resolve jt://secret/Abcd1234 --exec cmd /C "curl -s -H \"Authorization: Bearer %JT_SECRET%\" https://api.cloudflare.com/client/v4/user/tokens/verify"
     # PowerShell
     jt resolve jt://secret/Abcd1234 --exec powershell -NoProfile -Command "curl.exe -s -H \"Authorization: Bearer $env:JT_SECRET\" https://api.cloudflare.com/client/v4/user/tokens/verify"
     ```
     在 bash 里给整条命令用**单引号**，否则 `$JT_SECRET` 会在 `jt` 运行之前就被外层 shell 展开成空字符串。
2. 不知道有哪些密钥、属于哪个命名空间时运行 `jt ls --json`：只含 id、引用、名称、描述、遮罩预览，没有真值。
3. 真值是一次性运行时值：用完即弃，不写入文件、不写进 shell 配置、不打日志、不在后续输出里复述。

完成标准：

- 目标动作已用真值完成；
- 用的是 `--exec`（或 `jt env`），真值没有出现在任何工具结果里；
- 任何后续输出都不包含真值。

失败时（退出码 1，stderr 一行原因）向用户报告该引用无法解析或该命令失败；不要尝试绕过去读真值。子进程的退出码会原样透传。

## 反例

```powershell
# ❌ 想“先看看值”——jt 会拒绝，也不要换别的办法去看
jt resolve jt://secret/Abcd1234

# ❌ 在子进程里把值打印或落盘，等于把真值送进工具结果 / 日志
jt resolve jt://secret/Abcd1234 --exec cmd /C "echo %JT_SECRET%"
jt resolve jt://secret/Abcd1234 --exec bash -c 'env'
jt resolve jt://secret/Abcd1234 --exec bash -c 'echo "export TOKEN=$JT_SECRET" >> ~/.bashrc'
jt resolve jt://secret/Abcd1234 --exec bash -c 'curl -v ... 2>&1 | tee request.log'

# ❌ jt copy 是给人用的：它把明文放到剪贴板。Agent 不要调用它，也不要读剪贴板
jt copy jt://secret/Abcd1234
```

## 用户贴了明文密钥

如果用户直接在对话里给出 API Token、密码等明文：不要复述它、不要写进文件。请用户把它复制到剪贴板后运行

```powershell
jt grab <命名空间>/<VAR_NAME> --description '<用途>'
```

然后把生成的 `jt://secret/...` 引用贴回来。之后只用引用。

## 给密钥命名

只在用户想给某条记录命名时调用 `jt mv`，且满足其一：

- 用户明确说出名称，例如“这是 cf/CLOUDFLARE_API_TOKEN”；
- 上下文明确指向某个名称，例如“用我的 Cloudflare token”，且该引用还是临时名。

```powershell
jt mv jt://secret/<id> "<命名空间>/<VAR_NAME>"
```

名称约定为 `命名空间/VAR_NAME`，`VAR_NAME` 要是合法的环境变量名（这样 `jt env <命名空间>` 才能注入）。先 `jt ls` 看已有命名空间，沿用用户已有的；用户没说归哪组时只问命名空间，不要自己编。描述（`jt describe`）是明文、会进 Git 历史，只写用途、归属，不写任何密钥内容。
