# jt 解密工具（JtDecrypt.exe）

离线把 jt 的密文解回明文的 Windows 小程序，给**灾难恢复**用：电脑没了、jt 装不上、只剩 GitHub 或 Notion 里的备份时，有主密钥就能把每一条密码拿回来。日常取值仍然用 `jt`（`jt env` / `jt resolve` / GUI 的查看值），不要拿这个当日常工具——它的输出是明文。

## 用法

1. **主密钥**：三种来源任选
   - `jt key export` 导出的 base64 文件或字符串（备份包里的 `jt-master-key.txt` 就是）
   - WSL / Linux 的 `~/.config/jt/key`（32 字节原始文件）
   - 本机 jt 的 `%LOCALAPPDATA%\jt\key`（DPAPI 文件，只有同一台电脑的同一个 Windows 用户能读；按"读取本机 jt 的密钥"）
   载入后显示指纹（密钥 SHA-256 的前 8 位），和加密时的指纹一致才解得开。
2. **要解密的内容**：粘贴或导入（也可以把文件拖进窗口、或直接拖到 exe 上）。什么格式都行：
   - `vault.json` 整个文件
   - Notion 页「jt密钥管家」导出的 Markdown（里面的 `vault.json 原文` 代码块会被识别）或表格导出的 CSV
   - 在 Notion 表里选中几行复制出来的文字
   - 单独一段密文
3. **解密**：列表显示名称、明文（默认遮住，勾"显示明文"）、来源/描述。解不开的行标红——密钥不对或密文损坏。
4. 输出：双击或"复制选中的明文"（不进 Win+V 历史、不上云）、"复制全部"（`名称=明文` 每行一条）、"另存为文本…"（会再确认一次，文件是明文）。

识别原理：对文本里每一段像 base64 的字符都试着用 AES-GCM 解一次；GCM 自带认证，密钥不对或不是密文就直接失败，所以不会"解出"乱码。名称取同一行里的 `组名/VAR_NAME` 或引用，没有就取整行文字。

命令行：`JtDecrypt.exe --key 密钥文件 输入文件` 打开窗口并直接解密；`JtDecrypt.exe --check 密钥文件 输入文件 输出文件` 不开窗口，把每条的名称、明文的 SHA-256 前 16 位和长度写进输出文件（不写明文），用来验证构建是否正确。

## 构建

```powershell
cd tools\jt-decrypt
dotnet build -c Release
# 单文件、自包含（约 100 MB，任何 Win10/11 双击即用，备份包里放的是这个）
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

需要 .NET SDK 10。目标 `net10.0-windows`，只用标准库（`AesGcm`、`ProtectedData`、`System.Text.Json`），不联网。

## 验证

`--check` 的输出可以和 jt 自己解出来的值比对哈希，不暴露明文：

```powershell
.\publish\JtDecrypt.exe --check key.txt "$env:LOCALAPPDATA\jt\vault\vault.json" out.tsv
jt resolve cf/CLOUDFLARE_API_TOKEN --exec powershell -NoProfile -Command "[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($env:JT_SECRET))).Replace('-','').Substring(0,16).ToLower()"
```

两边哈希一致即正确。
