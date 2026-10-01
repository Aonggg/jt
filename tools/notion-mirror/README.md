# 把密钥库镜像到 Notion

一个放进 **vault 仓库**（不是这个 jt 仓库）的 GitHub Action：每次 `jt sync` 推送后，把 `vault.json` 写进一个 Notion 页面。写进去的和 GitHub 里的一样——名称、引用、遮罩预览、描述、时间、**密文**——没有明文，没有主密钥；没有主密钥这些密文解不开，所以它和 GitHub 私有仓库一样安全，也一样只是备份。

页面里会出现：

- 内嵌表「密钥条目」：每条密钥一行，按稳定的 ID 更新。改名、改值、改描述 → 更新对应行；删除 → 这一行标成「已删除」并记删除时间，不会消失；再次同步时没变化的行不动。
- 「vault.json 原文」代码块：整个文件的逐字备份，GitHub 不可用时可以直接恢复。
- 「最近同步」一行：时间、条目数、本次新增/更新/标记删除的数量、提交号。
- 「恢复步骤」和「主密钥」两节：静态说明。脚本永远不写「主密钥」一节，要不要把 `jt key export` 的结果手动贴进去由你决定——能打开这一页的人就能解开全部密文。

## 安装

1. Notion：建一个 internal integration（https://www.notion.so/my-integrations），拿到 `ntn_…` 令牌；在 Notion 里新建一页（比如「jt 密钥库」），页面右上角 … → 连接 → 选这个 integration。记下页面 ID（网址最后那串 32 位十六进制）。
2. vault 仓库：把本目录的两个文件放到
   - `.github/workflows/notion-mirror.yml`
   - `.github/scripts/notion_mirror.py`

   `jt sync` 只提交 `vault.json`，这两个文件要自己 `git add` 并提交一次。工作流里的分支名改成你的默认分支（`master` 或 `main`）。
3. 仓库设置：Secret `NOTION_TOKEN` = 令牌；Variable `NOTION_PAGE_ID` = 页面 ID。命令行：

   ```powershell
   gh secret set NOTION_TOKEN --repo <你>/jt-vault      # 从提示输入，不要写在命令里
   gh variable set NOTION_PAGE_ID --repo <你>/jt-vault --body <页面ID>
   ```
4. 推一次（或在 Actions 页手动运行 `notion-mirror`）。第一次运行会在页面里建表和各个小节，之后只更新。

本地试跑：`NOTION_TOKEN=… NOTION_PAGE_ID=… python3 notion_mirror.py`，默认读当前目录的 `vault.json`，`VAULT_PATH` 可以改。只依赖 Python 标准库。

## 边界

- Notion 令牌只存在 GitHub 的加密 Secrets 里，不在本机、不在 jt 里；GitHub 不可用时 Notion 也不更新（它跟着推送走）。
- 描述是明文，和在 GitHub 里一样会出现在 Notion；只写用途，不写密钥。
- Notion 的 rich text 每段最多 2000 字符，脚本会自动分段；一个代码块最多 100 段，也就是 `vault.json` 超过约 200 KB 时原文备份会写不下——几百条密钥以内不会碰到。
