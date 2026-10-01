#!/usr/bin/env python3
"""Mirror vault.json (ciphertext + metadata, never plaintext) into a Notion page.

Idempotent: rows are upserted by the stable secret id, unchanged rows are not
touched, rows for secrets that no longer exist are marked 已删除 (kept, never
removed). The full vault.json is also stored verbatim in a code block so the
file can be restored byte-for-byte without GitHub.

Environment: NOTION_TOKEN, NOTION_PAGE_ID; optional VAULT_PATH, GITHUB_SHA.
Standard library only.
"""
import datetime as dt
import hashlib
import json
import os
import sys
import time
import urllib.error
import urllib.request

API = "https://api.notion.com/v1"
VERSION = "2022-06-28"
DB_TITLE = "密钥条目"
CODE_MARKER = "jt-vault-mirror:vault.json"
SYNC_PREFIX = "最近同步："
CHUNK = 2000  # Notion rich_text item limit


def req(method, path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(API + path, data=data, method=method, headers={
        "Authorization": "Bearer " + os.environ["NOTION_TOKEN"],
        "Notion-Version": VERSION,
        "Content-Type": "application/json",
    })
    for attempt in range(5):
        try:
            with urllib.request.urlopen(r, timeout=60) as resp:
                return json.load(resp)
        except urllib.error.HTTPError as e:
            detail = e.read().decode(errors="replace")
            if e.code in (429, 500, 502, 503) and attempt < 4:
                time.sleep(float(e.headers.get("Retry-After", 2)) + attempt)
                continue
            sys.exit(f"Notion {method} {path} -> {e.code}: {detail}")
    raise RuntimeError("unreachable")


def text(s, code=False):
    s = s or ""
    out = []
    for i in range(0, max(len(s), 1), CHUNK):
        piece = {"type": "text", "text": {"content": s[i:i + CHUNK]}}
        if code:
            piece["annotations"] = {"code": True}
        out.append(piece)
    return out


def date(iso):
    return {"date": {"start": iso}} if iso else {"date": None}


def plain(rich):
    return "".join(t.get("plain_text", "") for t in rich or [])


def children(block_id):
    out, cursor = [], None
    while True:
        q = f"/blocks/{block_id}/children?page_size=100" + (f"&start_cursor={cursor}" if cursor else "")
        page = req("GET", q)
        out += page["results"]
        if not page.get("has_more"):
            return out
        cursor = page["next_cursor"]


def query_all(db_id):
    out, cursor = [], None
    while True:
        body = {"page_size": 100}
        if cursor:
            body["start_cursor"] = cursor
        page = req("POST", f"/databases/{db_id}/query", body)
        out += page["results"]
        if not page.get("has_more"):
            return out
        cursor = page["next_cursor"]


def fingerprint(entry):
    canon = json.dumps({k: entry.get(k) for k in ("id", "name", "description", "ciphertext", "preview", "created_at", "updated_at")}, sort_keys=True, ensure_ascii=False)
    return hashlib.sha256(canon.encode()).hexdigest()[:16]


def row_props(entry, now):
    return {
        "名称": {"title": text(entry.get("name") or entry["id"])},
        "引用": {"rich_text": text("jt://secret/" + entry["id"], code=True)},
        "ID": {"rich_text": text(entry["id"])},
        "预览": {"rich_text": text(entry.get("preview"), code=True)},
        "描述": {"rich_text": text(entry.get("description"))},
        "状态": {"select": {"name": "在用"}},
        "创建时间": date(entry.get("created_at")),
        "更新时间": date(entry.get("updated_at")),
        "同步时间": date(now),
        "删除时间": date(None),
        "密文": {"rich_text": text(entry["ciphertext"], code=True)},
        "指纹": {"rich_text": text(fingerprint(entry))},
    }


def ensure_database(page_id, blocks):
    for b in blocks:
        if b["type"] == "child_database" and b["child_database"]["title"] == DB_TITLE:
            return b["id"]
    db = req("POST", "/databases", {
        "parent": {"type": "page_id", "page_id": page_id},
        "is_inline": True,
        "title": [{"type": "text", "text": {"content": DB_TITLE}}],
        "properties": {
            "名称": {"title": {}},
            "引用": {"rich_text": {}},
            "ID": {"rich_text": {}},
            "预览": {"rich_text": {}},
            "描述": {"rich_text": {}},
            "状态": {"select": {"options": [{"name": "在用", "color": "green"}, {"name": "已删除", "color": "red"}]}},
            "创建时间": {"date": {}},
            "更新时间": {"date": {}},
            "同步时间": {"date": {}},
            "删除时间": {"date": {}},
            "密文": {"rich_text": {}},
            "指纹": {"rich_text": {}},
        },
    })
    print("created database", DB_TITLE)
    return db["id"]


def ensure_sections(page_id, blocks):
    """Append the vault.json code block, sync line and static sections if missing. Returns (code_block_id, sync_block_id)."""
    code_id = sync_id = None
    headings = set()
    for b in blocks:
        t = b["type"]
        if t == "code" and CODE_MARKER in plain(b["code"].get("caption")):
            code_id = b["id"]
        elif t == "paragraph" and plain(b["paragraph"]["rich_text"]).startswith(SYNC_PREFIX):
            sync_id = b["id"]
        elif t in ("heading_1", "heading_2", "heading_3"):
            headings.add(plain(b[t]["rich_text"]))
    new = []
    if code_id is None:
        new += [h2("vault.json 原文"),
                para("整个密钥库文件的逐字备份，每次同步覆盖。GitHub 不可用时把下面的内容原样存成 vault.json 即可（见恢复步骤）。"),
                {"object": "block", "type": "code", "code": {"language": "json", "rich_text": text("{}"), "caption": text(CODE_MARKER)}}]
    if sync_id is None:
        new += [h2("最近同步"), para(SYNC_PREFIX + "尚未同步")]
    if "恢复步骤" not in headings:
        repo = os.environ.get("GITHUB_REPOSITORY", "")
        vault_repo = f"https://github.com/{repo}.git" if repo else "<你的私有 vault 仓库地址>"
        new += [h2("恢复步骤"),
                num("装 jt：PowerShell 运行 irm https://raw.githubusercontent.com/Aonggg/jt/main/install.ps1 | iex（或从 https://github.com/Aonggg/jt/releases 下载）。"),
                num("导入主密钥：在保管主密钥的地方取出 jt key export 导出的那串 base64，复制后在新电脑运行 jt key import。没有主密钥，下面所有密文都无法解开。"),
                num(f"拿回密钥库：首选 jt init --repo {vault_repo} 然后 jt sync。GitHub 不可用时：先 jt init（不带 --repo），再把上面「vault.json 原文」代码块的内容原样保存为 %LOCALAPPDATA%\\jt\\vault\\vault.json。"),
                num("验证：jt ls 能列出条目、jt env <组名> -- cmd /C \"echo ok\" 能运行，说明一切恢复。"),
                para("这一页由 vault 仓库的 GitHub Action 在每次 jt sync 推送后自动更新；改名、改值、改描述会更新对应行，删除的条目保留为「已删除」。")]
    if "主密钥" not in headings:
        new += [h2("主密钥"),
                {"object": "block", "type": "callout", "callout": {"icon": {"type": "emoji", "emoji": "🔑"}, "color": "red_background",
                 "rich_text": text("自动同步永远不碰这一节。主密钥只在你自己的电脑上；如果决定把它备份在这里，由你本人运行 jt key export 后把剪贴板里的内容粘贴到下面。能打开这一页的人就能解开上面全部密文，请据此决定。")}}]
    if new:
        created = req("PATCH", f"/blocks/{page_id}/children", {"children": new})["results"]
        for b in created:
            if b["type"] == "code":
                code_id = b["id"]
            elif b["type"] == "paragraph" and plain(b["paragraph"]["rich_text"]).startswith(SYNC_PREFIX):
                sync_id = b["id"]
        print("appended", len(new), "blocks")
    return code_id, sync_id


def h2(s):
    return {"object": "block", "type": "heading_2", "heading_2": {"rich_text": text(s)}}


def para(s):
    return {"object": "block", "type": "paragraph", "paragraph": {"rich_text": text(s)}}


def num(s):
    return {"object": "block", "type": "numbered_list_item", "numbered_list_item": {"rich_text": text(s)}}


def main():
    page_id = os.environ["NOTION_PAGE_ID"]
    vault_path = os.environ.get("VAULT_PATH", "vault.json")
    raw = open(vault_path, encoding="utf-8").read()
    vault = json.loads(raw)
    entries = {e["id"]: e for e in vault.get("secrets", [])}
    now = dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")

    blocks = children(page_id)
    db_id = ensure_database(page_id, blocks)
    code_id, sync_id = ensure_sections(page_id, blocks)

    existing = {}
    for row in query_all(db_id):
        rid = plain(row["properties"].get("ID", {}).get("rich_text"))
        if rid:
            existing[rid] = row

    created = updated = deleted = unchanged = 0
    for sid, entry in entries.items():
        row = existing.get(sid)
        if row is None:
            req("POST", "/pages", {"parent": {"database_id": db_id}, "properties": row_props(entry, now)})
            created += 1
            print("created", sid, entry.get("name"))
        else:
            same_fp = plain(row["properties"].get("指纹", {}).get("rich_text")) == fingerprint(entry)
            active = (row["properties"].get("状态", {}).get("select") or {}).get("name") == "在用"
            if same_fp and active:
                unchanged += 1
            else:
                req("PATCH", f"/pages/{row['id']}", {"properties": row_props(entry, now)})
                updated += 1
                print("updated", sid, entry.get("name"))
        time.sleep(0.34)
    for sid, row in existing.items():
        if sid in entries:
            continue
        if (row["properties"].get("状态", {}).get("select") or {}).get("name") == "已删除":
            continue
        req("PATCH", f"/pages/{row['id']}", {"properties": {"状态": {"select": {"name": "已删除"}}, "删除时间": date(now), "同步时间": date(now)}})
        deleted += 1
        print("marked deleted", sid, plain(row["properties"]["名称"]["title"]))
        time.sleep(0.34)

    req("PATCH", f"/blocks/{code_id}", {"code": {"language": "json", "rich_text": text(raw), "caption": text(CODE_MARKER)}})
    sha = os.environ.get("GITHUB_SHA", "")[:7]
    summary = f"{SYNC_PREFIX}{now}（UTC）  条目 {len(entries)}  新增 {created}  更新 {updated}  标记删除 {deleted}  未变 {unchanged}" + (f"  提交 {sha}" if sha else "") + f"  vault 格式 v{vault.get('version')}"
    req("PATCH", f"/blocks/{sync_id}", {"paragraph": {"rich_text": text(summary)}})
    print(summary)


if __name__ == "__main__":
    main()
