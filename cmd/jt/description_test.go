package main

import (
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
)

func input(t *testing.T, value string) {
	t.Helper()
	f, err := os.CreateTemp(t.TempDir(), "stdin")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := f.WriteString(value); err != nil {
		t.Fatal(err)
	}
	if _, err := f.Seek(0, 0); err != nil {
		t.Fatal(err)
	}
	old := os.Stdin
	os.Stdin = f
	t.Cleanup(func() { os.Stdin = old; f.Close() })
}

func currentVault(t *testing.T) vault {
	t.Helper()
	c, err := loadConfig()
	if err != nil {
		t.Fatal(err)
	}
	v, _, err := openVault(c, false)
	if err != nil {
		t.Fatal(err)
	}
	return v
}

func vaultBytes(t *testing.T, dir string) []byte {
	t.Helper()
	data, err := os.ReadFile(filepath.Join(dir, "vault.json"))
	if err != nil {
		t.Fatal(err)
	}
	return data
}

func TestWriteDescriptionFlags(t *testing.T) {
	for _, tc := range []struct {
		name        string
		args        []string
		allowID     bool
		description *string
		positional  []string
		id          string
		clipboard   bool
		wantErr     bool
	}{
		{name: "omitted", args: []string{"app/KEY"}, positional: []string{"app/KEY"}},
		{name: "before name", args: []string{"--description", "生产环境 🚀", "app/KEY"}, description: strptr("生产环境 🚀"), positional: []string{"app/KEY"}},
		{name: "after name", args: []string{"app/KEY", "--description", "用途"}, description: strptr("用途"), positional: []string{"app/KEY"}},
		{name: "clear spaced", args: []string{"app/KEY", "--description", ""}, description: strptr(""), positional: []string{"app/KEY"}},
		{name: "clear equals", args: []string{"--description=", "app/KEY"}, description: strptr(""), positional: []string{"app/KEY"}},
		{name: "flag-like value", args: []string{"app/KEY", "--description=--from-clipboard"}, description: strptr("--from-clipboard"), positional: []string{"app/KEY"}},
		{name: "mixed flags", allowID: true, args: []string{"--id=Abcd1234", "app/KEY", "--from-clipboard", "--description=one=two"}, description: strptr("one=two"), positional: []string{"app/KEY"}, id: "Abcd1234", clipboard: true},
		{name: "end flags", args: []string{"--description", "note", "--", "--name"}, description: strptr("note"), positional: []string{"--name"}},
		{name: "missing value", args: []string{"app/KEY", "--description"}, wantErr: true},
		{name: "missing before option", args: []string{"app/KEY", "--description", "--from-clipboard"}, wantErr: true},
		{name: "duplicate description", args: []string{"--description=a", "--description=b", "app/KEY"}, wantErr: true},
		{name: "duplicate clipboard", args: []string{"--from-clipboard", "--from-clipboard", "app/KEY"}, wantErr: true},
		{name: "unknown flag", args: []string{"app/KEY", "--descripton=note"}, wantErr: true},
		{name: "unsupported set id", args: []string{"app/KEY", "--id", "Abcd1234"}, wantErr: true},
		{name: "invalid id", allowID: true, args: []string{"app/KEY", "--id="}, wantErr: true},
		{name: "missing id", allowID: true, args: []string{"app/KEY", "--id"}, wantErr: true},
		{name: "clipboard value", args: []string{"app/KEY", "--from-clipboard=true"}, wantErr: true},
		{name: "invalid UTF-8", args: []string{"app/KEY", "--description", string([]byte{0xff})}, wantErr: true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			opts, err := parseWriteFlags(tc.args, tc.allowID)
			if (err != nil) != tc.wantErr {
				t.Fatalf("error = %v, wantErr %v", err, tc.wantErr)
			}
			if tc.wantErr {
				return
			}
			if !reflect.DeepEqual(opts.description, tc.description) || !reflect.DeepEqual(opts.positional, tc.positional) || opts.id != tc.id || opts.fromClipboard != tc.clipboard {
				t.Fatalf("unexpected options: %+v", opts)
			}
		})
	}
}

func strptr(s string) *string { return &s }

func TestAddDescriptionAndSchema(t *testing.T) {
	for _, tc := range []struct {
		name    string
		args    []string
		desc    string
		version int
	}{
		{"omitted", nil, "", 1},
		{"empty", []string{"--description", ""}, "", 1},
		{"unicode", []string{"--description", "生产 / API 用途 🚀\n仅非秘密说明"}, "生产 / API 用途 🚀\n仅非秘密说明", 2},
	} {
		t.Run(tc.name, func(t *testing.T) {
			_, out := setup(t, nil)
			input(t, "test-secret-value\n")
			args := append([]string{"app/KEY", "--id", "Abcd1234"}, tc.args...)
			if err := add(args); err != nil {
				t.Fatal(err)
			}
			v := currentVault(t)
			if v.Version != tc.version || len(v.Secrets) != 1 || v.Secrets[0].Description != tc.desc {
				t.Fatalf("unexpected vault: %+v", v)
			}
			item := v.Secrets[0]
			value, err := open(bytes.Repeat([]byte{1}, 32), item.Ciphertext)
			if err != nil || value != "test-secret-value" || item.ID != "Abcd1234" || item.Preview != preview(value) {
				t.Fatal("description changed secret value, ID or preview")
			}
			if err := list([]string{"--json"}); err != nil {
				t.Fatal(err)
			}
			var items []map[string]any
			if err := json.Unmarshal(out.Bytes(), &items); err != nil {
				t.Fatal(err)
			}
			if len(items) != 1 || len(items[0]) != 7 || items[0]["description"] != tc.desc || items[0]["ref"] != prefix+item.ID {
				t.Fatalf("unexpected safe metadata shape: %v", items)
			}
			if strings.Contains(out.String(), item.Ciphertext) || strings.Contains(out.String(), value) || strings.Contains(out.String(), `"ciphertext"`) {
				t.Fatal("list JSON exposed encrypted or plaintext value")
			}
		})
	}
}

func TestDescribePreservesSecretAndClear(t *testing.T) {
	before := entry{ID: "Abcd1234", Name: "app/KEY", Ciphertext: "not-even-valid-ciphertext", Preview: "te******alue", CreatedAt: "2020-01-01T00:00:00Z", UpdatedAt: "2020-01-01T00:00:00Z"}
	dir, _ := setup(t, []entry{before})
	// A closed stdin proves metadata editing does not read or require the secret.
	input(t, "")
	os.Stdin.Close()
	desc := "生产 API；owner=平台团队 🚀"
	if err := describe([]string{prefix + before.ID, desc}); err != nil {
		t.Fatal(err)
	}
	v := currentVault(t)
	item := v.Secrets[0]
	if v.Version != 2 || item.Description != desc || item.UpdatedAt == before.UpdatedAt {
		t.Fatalf("metadata was not updated: %+v", item)
	}
	item.Description, item.UpdatedAt = before.Description, before.UpdatedAt
	if item != before {
		t.Fatalf("metadata edit changed secret fields: %+v", item)
	}
	unchanged := vaultBytes(t, dir)
	if err := describe([]string{before.Name, desc}); err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(unchanged, vaultBytes(t, dir)) {
		t.Fatal("no-op description edit rewrote the vault")
	}
	if err := describe([]string{before.Name, ""}); err != nil {
		t.Fatal(err)
	}
	v = currentVault(t)
	if v.Version != 2 || v.Secrets[0].Description != "" || v.Secrets[0].Ciphertext != before.Ciphertext {
		t.Fatal("clear downgraded schema or changed ciphertext")
	}
	if bytes.Contains(vaultBytes(t, dir), []byte(`"description"`)) {
		t.Fatal("empty stored description should be omitted")
	}
}

func TestDescriptionValidationDoesNotWrite(t *testing.T) {
	dir, _ := setup(t, []entry{{ID: "Abcd1234", Name: "app/KEY", Ciphertext: "unchanged"}})
	before := vaultBytes(t, dir)
	input(t, "")
	os.Stdin.Close()
	for _, args := range [][]string{nil, {"app/KEY"}, {"", "note"}, {"app/KEY", "a", "b"}, {"missing", "note"}, {prefix + "bad", "note"}, {"app/KEY", string([]byte{0xff})}} {
		if err := describe(args); err == nil {
			t.Fatalf("describe %q should fail", args)
		}
	}
	for _, command := range []func([]string) error{add, set} {
		if err := command([]string{"app/KEY", "--description"}); err == nil {
			t.Fatal("missing description should fail before input")
		}
	}
	if !bytes.Equal(before, vaultBytes(t, dir)) {
		t.Fatal("invalid command modified vault")
	}
}

func TestDescriptionLifecycle(t *testing.T) {
	before := entry{ID: "Abcd1234", Name: "app/KEY", Ciphertext: "old", Preview: "***", CreatedAt: "2020-01-01T00:00:00Z"}
	setup(t, []entry{before})
	if err := describe([]string{before.Name, "purpose"}); err != nil {
		t.Fatal(err)
	}
	if err := rename([]string{prefix + before.ID, "renamed/KEY"}); err != nil {
		t.Fatal(err)
	}
	v := currentVault(t)
	if v.Secrets[0].Description != "purpose" || v.Secrets[0].Ciphertext != before.Ciphertext || v.Secrets[0].ID != before.ID {
		t.Fatal("rename lost metadata or changed identity/ciphertext")
	}
	for _, tc := range []struct {
		args []string
		desc string
	}{
		{nil, "purpose"},
		{[]string{"--description=用途更新"}, "用途更新"},
		{[]string{"--description="}, ""},
	} {
		input(t, "replacement-value\n")
		if err := set(append([]string{prefix + before.ID}, tc.args...)); err != nil {
			t.Fatal(err)
		}
		v = currentVault(t)
		item := v.Secrets[0]
		value, err := open(bytes.Repeat([]byte{1}, 32), item.Ciphertext)
		if err != nil || value != "replacement-value" || v.Version != 2 || item.ID != before.ID || item.CreatedAt != before.CreatedAt || item.Description != tc.desc {
			t.Fatal("set lost identity/description or failed to update value")
		}
	}
	if err := describe([]string{prefix + before.ID, "final purpose"}); err != nil {
		t.Fatal(err)
	}
	if err := remove([]string{prefix + before.ID}); err != nil {
		t.Fatal(err)
	}
	v = currentVault(t)
	if v.Version != 2 || len(v.Secrets) != 0 {
		t.Fatal("removing the final described entry downgraded or left metadata")
	}
}

func TestSetDescriptionUpgradesLegacy(t *testing.T) {
	setup(t, []entry{{ID: "Abcd1234", Name: "app/KEY"}})
	input(t, "new-value")
	if err := set([]string{"app/KEY", "--description", "purpose"}); err != nil {
		t.Fatal(err)
	}
	v := currentVault(t)
	if v.Version != 2 || v.Secrets[0].Description != "purpose" {
		t.Fatal("set did not upgrade schema for new metadata")
	}
}

func TestLegacyReadAndSchemaGuard(t *testing.T) {
	dir, out := setup(t, nil)
	legacy := []byte(`{"version":1,"secrets":[{"id":"Abcd1234","name":"app/KEY","ciphertext":"opaque","preview":"***"}]}`)
	if err := os.WriteFile(filepath.Join(dir, "vault.json"), legacy, 0600); err != nil {
		t.Fatal(err)
	}
	if err := list([]string{"--json"}); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), `"description": ""`) || !bytes.Equal(legacy, vaultBytes(t, dir)) {
		t.Fatal("legacy read omitted description or mutated vault")
	}
	if err := describe([]string{"app/KEY", ""}); err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(legacy, vaultBytes(t, dir)) {
		t.Fatal("legacy no-op clear rewrote vault")
	}
	c, _ := loadConfig()
	for _, version := range []int{0, 3} {
		if err := saveVault(c, vault{Version: version}); err == nil {
			t.Fatal("save accepted unsupported schema")
		}
		if !bytes.Equal(legacy, vaultBytes(t, dir)) {
			t.Fatal("unsupported save modified vault")
		}
	}
	if err := os.WriteFile(filepath.Join(dir, "vault.json"), []byte(`{"version":3,"secrets":[]}`), 0600); err != nil {
		t.Fatal(err)
	}
	if _, _, err := openVault(c, false); err == nil {
		t.Fatal("open accepted unsupported schema")
	}
}

func TestDescriptionSafeTextAndSearch(t *testing.T) {
	desc := "部署 Production 🚀 | note\nforged\r\t\x1b[31m\u202ehidden <script>"
	_, out := setup(t, []entry{{ID: "Abcd1234", Name: "app/KEY", Description: desc, Ciphertext: "never-output-this", Preview: "masked"}})
	if err := list(nil); err != nil {
		t.Fatal(err)
	}
	text := out.String()
	if strings.Count(text, "\n") != 1 || strings.ContainsAny(text, "\x1b\r\t\u202e") || !strings.Contains(text, "部署 Production 🚀") || strings.Contains(text, "never-output-this") {
		t.Fatalf("unsafe or missing description in text output: %q", text)
	}
	for _, query := range []string{"production", "部署", "Abcd"} {
		out.Reset()
		if err := list([]string{"--json", query}); err != nil {
			t.Fatal(err)
		}
		var items []listItem
		if err := json.Unmarshal(out.Bytes(), &items); err != nil || len(items) != 1 || items[0].Description != desc {
			t.Fatalf("query/JSON description roundtrip failed: %v %s", err, out)
		}
	}
}

func TestDescriptionSyncRoundTrip(t *testing.T) {
	dir, out := setup(t, []entry{{ID: "Abcd1234", Name: "app/KEY", Ciphertext: "opaque", Preview: "***"}})
	remote := t.TempDir()
	git(t, remote, "init", "--bare")
	git(t, dir, "init")
	git(t, dir, "config", "user.name", "Test")
	git(t, dir, "config", "user.email", "test@example.invalid")
	git(t, dir, "remote", "add", "origin", remote)
	if err := syncVault(nil); err != nil {
		t.Fatal(err)
	}
	git(t, dir, "branch", "--set-upstream-to=origin/main", "main")
	clone := filepath.Join(t.TempDir(), "clone")
	git(t, remote, "clone", remote, clone)
	if err := describe([]string{"app/KEY", "同步用途 🚀"}); err != nil {
		t.Fatal(err)
	}
	if st := readStatus(t, out); !st.Dirty {
		t.Fatal("description-only edit was not reported dirty")
	}
	if err := syncVault(nil); err != nil {
		t.Fatal(err)
	}
	if st := readStatus(t, out); st.Dirty || st.Ahead == nil || *st.Ahead != 0 {
		t.Fatalf("description edit was not fully synced: %+v", st)
	}
	t.Setenv("JT_VAULT_DIR", clone)
	if err := syncVault(nil); err != nil {
		t.Fatal(err)
	}
	v := currentVault(t)
	if v.Version != 2 || v.Secrets[0].Description != "同步用途 🚀" || v.Secrets[0].Ciphertext != "opaque" || v.Secrets[0].ID != "Abcd1234" {
		t.Fatal("sync lost description, schema or secret identity")
	}
}

func TestSaveVaultDoesNotReorderCaller(t *testing.T) {
	setup(t, nil)
	c, _ := loadConfig()
	v := vault{Version: 1, Secrets: []entry{
		{ID: "ZZZZZZZZ", Name: "z/KEY", Description: "last"},
		{ID: "AAAAAAAA", Name: "a/KEY", Description: "first"},
	}}
	_, item, err := find(v, "z/KEY")
	if err != nil {
		t.Fatal(err)
	}
	if err := saveVault(c, v); err != nil {
		t.Fatal(err)
	}
	if item.ID != "ZZZZZZZZ" || v.Secrets[0].Name != "z/KEY" {
		t.Fatal("save sorting changed the caller's entry pointer")
	}
	loaded := currentVault(t)
	if loaded.Secrets[0].ID != "AAAAAAAA" || loaded.Secrets[1].Description != "last" {
		t.Fatal("persisted entries were not sorted or lost descriptions")
	}
}

func TestDescriptionsDoNotChangeSecretConsumption(t *testing.T) {
	setup(t, nil)
	input(t, "fixture-value")
	if err := add([]string{"app/KEY", "--description", "INJECTED=not-an-environment-variable"}); err != nil {
		t.Fatal(err)
	}
	t.Setenv("INJECTED", "")
	if err := env([]string{"app", "--", "sh", "-c", `test "$KEY" = fixture-value && test -z "$INJECTED"`}); err != nil {
		t.Fatalf("env changed value or injected metadata: %v", err)
	}
	if err := resolve([]string{"app/KEY", "--exec", "sh", "-c", `test "$JT_SECRET" = fixture-value && test -z "$INJECTED"`}); err != nil {
		t.Fatalf("resolve changed value or injected metadata: %v", err)
	}
}
