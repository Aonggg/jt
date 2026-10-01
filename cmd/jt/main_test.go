package main

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
)

// setup points jt at a temp home with a key and the given vault, and captures stdout.
func setup(t *testing.T, secrets []entry) (string, *bytes.Buffer) {
	t.Helper()
	home := t.TempDir()
	t.Setenv("JT_HOME", home)
	t.Setenv("JT_VAULT_DIR", "")
	t.Setenv("JT_KEY_FILE", "")
	if err := os.WriteFile(filepath.Join(home, "key"), bytes.Repeat([]byte{1}, 32), 0600); err != nil {
		t.Fatal(err)
	}
	data, err := json.Marshal(vault{Version: 1, Secrets: secrets})
	if err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(filepath.Join(home, "vault"), 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(home, "vault", "vault.json"), data, 0600); err != nil {
		t.Fatal(err)
	}
	buf := &bytes.Buffer{}
	old := stdout
	stdout = buf
	t.Cleanup(func() { stdout = old })
	return filepath.Join(home, "vault"), buf
}

func TestListJSON(t *testing.T) {
	_, out := setup(t, []entry{
		{ID: "BBBBBBBB", Name: "myapp/OPENAI_API_KEY", Ciphertext: "secret-bytes", Preview: "sk****3fA2", CreatedAt: "2026-09-22T05:43:04Z", UpdatedAt: "2026-09-23T05:43:04Z"},
		{ID: "AAAAAAAA", Name: "infra/RAILWAY | TOKEN", Ciphertext: "secret-bytes", Preview: "****"},
	})
	if err := list([]string{"--json"}); err != nil {
		t.Fatal(err)
	}
	if strings.Contains(out.String(), "ciphertext") || strings.Contains(out.String(), "secret-bytes") {
		t.Fatalf("ciphertext leaked: %s", out)
	}
	var items []map[string]any
	if err := json.Unmarshal(out.Bytes(), &items); err != nil {
		t.Fatal(err)
	}
	if len(items) != 2 || items[0]["name"] != "infra/RAILWAY | TOKEN" || items[1]["name"] != "myapp/OPENAI_API_KEY" {
		t.Fatalf("unexpected order: %v", items)
	}
	if items[0]["ref"] != "jt://secret/AAAAAAAA" || items[0]["created_at"] != nil || items[0]["updated_at"] != nil {
		t.Fatalf("unexpected legacy entry: %v", items[0])
	}
	if items[1]["created_at"] != "2026-09-22T05:43:04Z" || items[1]["preview"] != "sk****3fA2" {
		t.Fatalf("unexpected entry: %v", items[1])
	}
}

func TestListJSONQueryAndEmpty(t *testing.T) {
	_, out := setup(t, []entry{
		{ID: "AAAAAAAA", Name: "myapp/OPENAI_API_KEY"},
		{ID: "BBBBBBBB", Name: "infra/TOKEN"},
	})
	if err := list([]string{"OPENAI", "--json"}); err != nil {
		t.Fatal(err)
	}
	var items []listItem
	if err := json.Unmarshal(out.Bytes(), &items); err != nil {
		t.Fatal(err)
	}
	if len(items) != 1 || items[0].ID != "AAAAAAAA" {
		t.Fatalf("query not applied: %v", items)
	}
	out.Reset()
	if err := list([]string{"--json", "nothing-matches"}); err != nil {
		t.Fatal(err)
	}
	if strings.TrimSpace(out.String()) != "[]" {
		t.Fatalf("empty result should be [], got %q", out)
	}
}

func TestListTextUnchanged(t *testing.T) {
	_, out := setup(t, []entry{{ID: "AAAAAAAA", Name: "myapp/KEY", Preview: "ab**cd"}})
	if err := list(nil); err != nil {
		t.Fatal(err)
	}
	if out.String() != "myapp/KEY | jt://secret/AAAAAAAA | ab**cd\n" {
		t.Fatalf("text output changed: %q", out)
	}
}

func git(t *testing.T, dir string, args ...string) {
	t.Helper()
	cmd := exec.Command("git", append([]string{"-C", dir, "-c", "user.name=t", "-c", "user.email=t@t", "-c", "init.defaultBranch=main"}, args...)...)
	if out, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("git %v: %v\n%s", args, err, out)
	}
}

func readStatus(t *testing.T, out *bytes.Buffer) vaultStatus {
	t.Helper()
	out.Reset()
	if err := status([]string{"--json"}); err != nil {
		t.Fatal(err)
	}
	var st vaultStatus
	if err := json.Unmarshal(out.Bytes(), &st); err != nil {
		t.Fatal(err)
	}
	return st
}

func TestStatusWithoutGit(t *testing.T) {
	_, out := setup(t, nil)
	st := readStatus(t, out)
	if st.Git || st.Dirty || st.Ahead != nil {
		t.Fatalf("non-git vault: %+v", st)
	}
}

func TestStatusDirtyAndAhead(t *testing.T) {
	dir, out := setup(t, nil)
	remote := t.TempDir()
	git(t, remote, "init", "--bare")
	git(t, dir, "init")
	if st := readStatus(t, out); !st.Git || !st.Dirty || st.Ahead != nil {
		t.Fatalf("untracked vault.json should be dirty with no remote: %+v", st)
	}
	git(t, dir, "add", "vault.json")
	git(t, dir, "commit", "-m", "init")
	git(t, dir, "remote", "add", "origin", remote)
	git(t, dir, "push", "origin", "HEAD")
	git(t, dir, "fetch", "origin")
	if st := readStatus(t, out); st.Dirty || st.Ahead == nil || *st.Ahead != 0 {
		t.Fatalf("clean pushed vault: %+v", st)
	}
	if err := os.WriteFile(filepath.Join(dir, "vault.json"), []byte(`{"version":1,"secrets":[]}`), 0600); err != nil {
		t.Fatal(err)
	}
	if st := readStatus(t, out); !st.Dirty {
		t.Fatalf("modified vault.json should be dirty: %+v", st)
	}
	git(t, dir, "commit", "-am", "change")
	if st := readStatus(t, out); st.Dirty || st.Ahead == nil || *st.Ahead != 1 {
		t.Fatalf("one unpushed commit: %+v", st)
	}
}

// A second machine bootstraps with `jt init --repo URL; jt sync` while the
// remote already holds a vault, and the first machine can sync repeatedly
// without configuring an upstream or a git identity by hand.
func TestInitClonesExistingRemoteAndSyncRepeats(t *testing.T) {
	// No global or system git config: a fresh machine has no user.name/user.email.
	t.Setenv("GIT_CONFIG_GLOBAL", filepath.Join(t.TempDir(), "absent"))
	t.Setenv("GIT_CONFIG_NOSYSTEM", "1")
	remote := t.TempDir()
	git(t, remote, "init", "--bare")
	dir, out := setup(t, []entry{{ID: "Abcd1234", Name: "app/KEY", Ciphertext: "opaque", Preview: "***"}})
	if err := initVault([]string{"--repo", remote}); err != nil {
		t.Fatal(err)
	}
	for i := range 2 {
		if err := syncVault(nil); err != nil {
			t.Fatalf("sync #%d on the first machine: %v", i+1, err)
		}
	}
	if st := readStatus(t, out); st.Dirty || st.Ahead == nil || *st.Ahead != 0 {
		t.Fatalf("first machine after sync: %+v", st)
	}
	if author, err := gitOutput(dir, "log", "-1", "--format=%an <%ae>"); err != nil || author != "jt <jt@localhost>" {
		t.Fatalf("commit without a configured identity should use the jt fallback, got %q (%v)", author, err)
	}
	if crlf, err := gitOutput(dir, "config", "core.autocrlf"); err != nil || crlf != "false" {
		t.Fatalf("vault repo should pin core.autocrlf=false, got %q (%v)", crlf, err)
	}
	if err := os.WriteFile(filepath.Join(dir, "vault.json"), []byte(`{"version":1,"secrets":[]}`), 0600); err != nil {
		t.Fatal(err)
	}
	if err := syncVault(nil); err != nil {
		t.Fatal(err)
	}

	second := t.TempDir()
	t.Setenv("JT_HOME", second)
	if err := initVault([]string{"--repo", remote}); err != nil {
		t.Fatal(err)
	}
	if err := syncVault(nil); err != nil {
		t.Fatalf("second machine sync: %v", err)
	}
	data, err := os.ReadFile(filepath.Join(second, "vault", "vault.json"))
	if err != nil || string(data) != `{"version":1,"secrets":[]}` {
		t.Fatalf("second machine did not clone the pushed vault: %s (%v)", data, err)
	}
	if st := readStatus(t, out); !st.Git || st.Dirty || st.Ahead == nil || *st.Ahead != 0 {
		t.Fatalf("second machine status: %+v", st)
	}
	if _, err := os.Stat(filepath.Join(second, "key")); err != nil {
		t.Fatal("an empty cloned vault gets a fresh key")
	}

	// A machine joining a vault that already holds ciphertext must not mint its
	// own key; `jt key import` then works without moving anything away.
	if err := os.WriteFile(filepath.Join(second, "vault", "vault.json"), []byte(`{"version":1,"secrets":[{"id":"Abcd1234","name":"app/KEY","ciphertext":"opaque","preview":"***"}]}`), 0600); err != nil {
		t.Fatal(err)
	}
	if err := syncVault(nil); err != nil {
		t.Fatal(err)
	}
	third := t.TempDir()
	t.Setenv("JT_HOME", third)
	if err := initVault([]string{"--repo", remote}); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(filepath.Join(third, "key")); !os.IsNotExist(err) {
		t.Fatal("init must not create a key for a vault that already has entries")
	}
	useFakeClipboard(t, base64.StdEncoding.EncodeToString(bytes.Repeat([]byte{1}, 32)), false)
	if err := keyCommand([]string{"import"}); err != nil {
		t.Fatalf("key import after init: %v", err)
	}
	if err := list(nil); err != nil {
		t.Fatalf("vault unreadable after import: %v", err)
	}
}
