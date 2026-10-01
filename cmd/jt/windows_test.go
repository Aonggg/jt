//go:build windows

package main

import (
	"bytes"
	"encoding/base64"
	"errors"
	"flag"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// TestHelperProcess is the child for env/resolve tests: it exits 0 only when
// the variable named by the first argument holds the second argument and the
// INJECTED description decoy is absent. Parents set JT_TEST_HELPER=1 and run
// the test binary with `-test.run=^TestHelperProcess$ -- VAR VALUE`.
func TestHelperProcess(t *testing.T) {
	if os.Getenv("JT_TEST_HELPER") != "1" {
		return
	}
	args := flag.Args()
	if len(args) != 2 || os.Getenv(args[0]) != args[1] || os.Getenv("INJECTED") != "" {
		os.Exit(1)
	}
	os.Exit(0)
}

type fakeClipboard struct {
	text      string
	sensitive bool
	writes    int
	cleared   int
}

// useFakeClipboard replaces the Win32 clipboard for the test so nothing
// touches the developer's clipboard and history checks are deterministic.
func useFakeClipboard(t *testing.T, initial string, historyOn bool) *fakeClipboard {
	t.Helper()
	f := &fakeClipboard{text: initial}
	oldRead, oldWrite, oldEnabled, oldClear := readClipboard, writeClipboard, clipboardHistoryEnabled, clearClipboardHistory
	readClipboard = func() (string, error) { return f.text, nil }
	writeClipboard = func(text string, sensitive bool) error {
		f.text, f.sensitive = text, sensitive
		f.writes++
		return nil
	}
	clipboardHistoryEnabled = func() bool { return historyOn }
	clearClipboardHistory = func() error { f.cleared++; return nil }
	t.Cleanup(func() {
		readClipboard, writeClipboard, clipboardHistoryEnabled, clearClipboardHistory = oldRead, oldWrite, oldEnabled, oldClear
	})
	return f
}

func TestGrabStoresClipboardAndLeavesReference(t *testing.T) {
	setup(t, nil)
	clip := useFakeClipboard(t, "cf-token-value\r\n", true)
	if err := grab([]string{"cf/API_TOKEN", "--id", "Abcd1234", "--description", "Cloudflare"}); err != nil {
		t.Fatal(err)
	}
	if clip.text != prefix+"Abcd1234" || clip.sensitive || clip.cleared != 0 {
		t.Fatalf("clipboard after grab: %+v", clip)
	}
	v := currentVault(t)
	value, err := open(bytes.Repeat([]byte{1}, 32), v.Secrets[0].Ciphertext)
	if err != nil || value != "cf-token-value" || v.Secrets[0].Description != "Cloudflare" {
		t.Fatalf("stored entry: %+v (%v)", v.Secrets[0], err)
	}
	// A duplicate name must fail before the clipboard is touched.
	clip.text = "other"
	if err := grab([]string{"cf/API_TOKEN"}); err == nil || clip.text != "other" {
		t.Fatalf("duplicate grab: err=%v clipboard=%q", err, clip.text)
	}
	clip.text = ""
	if err := grab([]string{"cf/EMPTY"}); err == nil {
		t.Fatal("empty clipboard should be rejected")
	}
	clip.text = "second"
	if err := grab([]string{"cf/SECOND", "--clear-history"}); err != nil {
		t.Fatal(err)
	}
	if clip.cleared != 1 || !strings.HasPrefix(clip.text, prefix) {
		t.Fatalf("clear-history grab: %+v", clip)
	}
	if len(currentVault(t).Secrets) != 2 {
		t.Fatal("failed grabs must not store entries")
	}
}

func TestCopyIsSensitiveAndRefIsNot(t *testing.T) {
	ciphertext, err := seal(bytes.Repeat([]byte{1}, 32), "plain-value")
	if err != nil {
		t.Fatal(err)
	}
	setup(t, []entry{{ID: "Abcd1234", Name: "app/KEY", Ciphertext: ciphertext, Preview: "pl***alue"}})
	clip := useFakeClipboard(t, "", false)
	if err := copyValue([]string{"app/KEY"}); err != nil {
		t.Fatal(err)
	}
	if clip.text != "plain-value" || !clip.sensitive {
		t.Fatalf("copy should put the plaintext on the clipboard as sensitive: %+v", clip)
	}
	if err := ref([]string{prefix + "Abcd1234"}); err != nil {
		t.Fatal(err)
	}
	if clip.text != prefix+"Abcd1234" || clip.sensitive {
		t.Fatalf("ref should put the plain reference on the clipboard: %+v", clip)
	}
	for _, args := range [][]string{nil, {"missing"}, {"a", "b"}} {
		if err := copyValue(args); err == nil {
			t.Fatalf("copy %q should fail", args)
		}
		if err := ref(args); err == nil {
			t.Fatalf("ref %q should fail", args)
		}
	}
}

func TestResolveNeverPrintsAndRunsWithoutShell(t *testing.T) {
	setup(t, nil)
	t.Setenv("JT_TEST_HELPER", "1")
	input(t, "fixture-value")
	if err := add([]string{"app/KEY"}); err != nil {
		t.Fatal(err)
	}
	err := resolve([]string{"app/KEY"})
	if err == nil || !strings.Contains(err.Error(), "--exec") {
		t.Fatalf("resolve without --exec must refuse: %v", err)
	}
	err = resolve([]string{"app/KEY", "--exec", "echo %JT_SECRET%"})
	if err == nil || !strings.Contains(err.Error(), "cmd /C") {
		t.Fatalf("a shell string must fail with a hint, got: %v", err)
	}
	err = resolve([]string{"app/KEY", "--env", "MY_VAR", "--exec", os.Args[0], "-test.run=^TestHelperProcess$", "--", "MY_VAR", "fixture-value"})
	if err != nil {
		t.Fatalf("--env name not honoured: %v", err)
	}
	err = resolve([]string{"app/KEY", "--exec", "cmd", "/C", "exit 7"})
	var status exitStatusError
	if !errors.As(err, &status) || status.code != 7 {
		t.Fatalf("child exit status not propagated: %v", err)
	}
}

func TestAddTrimsOneCRLF(t *testing.T) {
	setup(t, nil)
	for name, stdin := range map[string]string{"app/ONE": "value with trailing newline\r\n", "app/TWO": "two\r\n\r\n", "app/LF": "lf\n"} {
		input(t, stdin)
		if err := add([]string{name}); err != nil {
			t.Fatal(err)
		}
	}
	v := currentVault(t)
	for name, want := range map[string]string{"app/ONE": "value with trailing newline", "app/TWO": "two\r\n", "app/LF": "lf"} {
		_, item, err := find(v, name)
		if err != nil {
			t.Fatal(err)
		}
		if value, err := open(bytes.Repeat([]byte{1}, 32), item.Ciphertext); err != nil || value != want {
			t.Fatalf("%s stored %q, want %q", name, value, want)
		}
	}
}

func TestKeyIsProtectedAndReadsRawKeys(t *testing.T) {
	home := t.TempDir()
	keyPath := filepath.Join(home, "key")
	first, err := loadKey(keyPath, true)
	if err != nil || len(first) != 32 {
		t.Fatalf("create key: %v", err)
	}
	data, err := os.ReadFile(keyPath)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.HasPrefix(data, []byte(keyMagic)) || bytes.Contains(data, first) {
		t.Fatal("key file must be a DPAPI blob that does not contain the raw key")
	}
	again, err := loadKey(keyPath, false)
	if err != nil || !bytes.Equal(first, again) {
		t.Fatalf("protected key did not round-trip: %v", err)
	}
	if _, err := decodeKey(append([]byte(keyMagic), data[len(keyMagic)+10:]...)); err == nil {
		t.Fatal("truncated blob must be rejected")
	}
	if _, err := decodeKey([]byte("short")); err == nil {
		t.Fatal("garbage must be rejected")
	}
	raw := bytes.Repeat([]byte{7}, 32)
	if got, err := decodeKey(raw); err != nil || !bytes.Equal(got, raw) {
		t.Fatalf("raw 32-byte key from another platform must load: %v", err)
	}
	if _, err := loadKey(filepath.Join(home, "missing"), false); err == nil {
		t.Fatal("missing key without create must fail")
	}
}

func TestKeyExportImport(t *testing.T) {
	setup(t, nil) // raw key of 32 x 0x01
	clip := useFakeClipboard(t, "", false)
	if err := keyCommand([]string{"export"}); err != nil {
		t.Fatal(err)
	}
	if !clip.sensitive || clip.text != base64.StdEncoding.EncodeToString(bytes.Repeat([]byte{1}, 32)) {
		t.Fatalf("export must put the base64 key on the clipboard as sensitive: %+v", clip)
	}
	if err := keyCommand([]string{"import"}); err == nil {
		t.Fatal("import must refuse to overwrite an existing key")
	}
	other := t.TempDir()
	t.Setenv("JT_HOME", other)
	if err := keyCommand([]string{"import"}); err != nil {
		t.Fatal(err)
	}
	key, err := loadKey(filepath.Join(other, "key"), false)
	if err != nil || !bytes.Equal(key, bytes.Repeat([]byte{1}, 32)) {
		t.Fatalf("imported key differs: %v", err)
	}
	third := t.TempDir()
	t.Setenv("JT_HOME", third)
	for _, args := range [][]string{{"import", "bm90LWEta2V5"}, {"import", "not base64!"}, {"export", "extra"}, {"rotate"}, nil} {
		if err := keyCommand(args); err == nil {
			t.Fatalf("key %q should fail", args)
		}
	}
	if _, err := os.Stat(filepath.Join(third, "key")); !os.IsNotExist(err) {
		t.Fatal("rejected imports must not create a key")
	}
	if err := keyCommand([]string{"import", " " + clip.text + "\r\n"}); err != nil {
		t.Fatalf("import from argument: %v", err)
	}
}
