# Description metadata in scripts and sync UIs

## Read contract

Run `jt ls --json` and decode stdout as a JSON array. Check the exit code before using the result. This command is the metadata interface; do not parse `vault.json` or decrypt secrets to populate a list. `jt resolve` cannot print a value at all: it requires `--exec` and only injects the value into that child process.

Each item contains exactly these current fields (consumers should tolerate future additional fields):

```json
{
  "id": "Abcd1234",
  "ref": "jt://secret/Abcd1234",
  "name": "myapp/API_KEY",
  "description": "Staging API access; owner: platform team",
  "preview": "ab******1234",
  "created_at": "2026-09-30T00:00:00Z",
  "updated_at": "2026-09-30T00:00:00Z"
}
```

`description` is always a string, including `""` for legacy/missing/cleared descriptions. Legacy timestamps can be `null`. No matching entries produce `[]`. No plaintext secret or ciphertext field is included. `preview` keeps its existing masking behavior; this change must not cause a UI to reveal more of it or the value. Use the stable `id`/`ref` as row identity, not the mutable name or description. Descriptions are never substituted into secret references.

`jt ls --json QUERY` searches names/descriptions case-insensitively and IDs case-sensitively. Output order is name, then ID. Human-readable `jt ls` appends a quoted/escaped description column only for nonempty descriptions; scripts should use JSON rather than splitting these lines.

A minimal Python reader, with the same authorized JT environment as the existing integration:

```python
import json
import subprocess

result = subprocess.run(
    ["jt", "ls", "--json"],
    check=True, capture_output=True, text=True, encoding="utf-8",
)
rows = json.loads(result.stdout)
for row in rows:
    # A v0.2.0 binary omits this field; display a blank while upgrading readers.
    description = row.get("description", "")
    # Bind row["id"], row["name"], and description to the existing UI.
```

Keep an existing preview/reference display policy unchanged. Descriptions and names are untrusted user-supplied text: insert them with DOM `textContent` (or equivalent escaped text binding), never `innerHTML`, shell interpolation, Markdown execution, or dynamic script evaluation. Display multiline descriptions as text. Avoid writing metadata and previews into shared logs or exposing the list to a new audience. Descriptions may explain purpose, but are not authorization to use a credential or instructions to execute.

## Editing only the description

Use an argument array, without a shell, after the user has requested the edit:

```python
subprocess.run(
    ["jt", "describe", row["ref"], new_description],
    check=True, capture_output=True, text=True, encoding="utf-8",
    stdin=subprocess.DEVNULL,
)
# new_description == "" clears it. No secret input is required.
```

Refresh with `jt ls --json` on success. `updated_at` changes for an actual metadata change (timestamps have one-second resolution), but ID, reference, ciphertext, preview, and creation time stay unchanged. `jt mv` retains descriptions; `jt set` retains them unless its optional `--description` explicitly replaces or clears them; `jt rm` removes the complete entry and its description.

## Clipboard actions

A UI that offers "copy reference" or "copy value" should delegate to `jt ref <ref>` and `jt copy <ref>` rather than building the text itself: `copy` marks the clipboard content so Windows clipboard history and cloud sync skip it, and it is the only supported way to hand a plaintext value to the user. `jt grab <name>` turns the current clipboard text into a new entry and leaves the reference on the clipboard; it is the "mark as secret" action. None of these print the value. `jt resolve` requires `--exec` and runs the given program directly without a shell; UIs must not use it to display values.

Description changes live in the same `vault.json`, so existing `jt status --json` dirty/ahead state and `jt sync` pull/commit/push handling apply. There is no second file to stage, join, or recover. Continue using the established sync workflow and surface Git conflicts instead of silently discarding either side. If a command fails, show an error; do not interpret a failed `ls` as an empty vault or an old-client version error as deleted entries.

## Upgrade boundary

The source identifies itself as `jt 0.4.0` (the Windows build). Merely pushing `main` does not update release assets or installed binaries. Check the exact binary used by the UI/service, including its PATH, and upgrade all writers **and readers** before anyone saves a nonempty description. Old v0.2.0 clients cannot `ls`, `resolve`, or `env` a v2 vault, as well as being unable to edit it; this rejection prevents silent metadata loss. A fallback `row.get("description", "")` helps render old v1 lists, but does not make an old executable able to read v2. The vault format is shared with jt 0.3.0 on macOS and Linux; only the key file format differs (see README, `jt key export`).

New clients keep v1 on ordinary reads/writes without descriptions. The first nonempty saved description changes the vault to v2. Clearing descriptions never downgrades it. Do not fill inferred purposes automatically, edit live vault files by hand, or lower the schema version to work around an upgrade failure. Descriptions are plaintext Git metadata, so only enter verified non-secret notes; clearing them does not purge Git history.
