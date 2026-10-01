//go:build windows

package main

import (
	"bytes"
	"os"
	"path/filepath"
	"testing"
)

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
	if _, err := decodeKeyFile(append([]byte(keyMagic), data[len(keyMagic)+10:]...)); err == nil {
		t.Fatal("truncated blob must be rejected")
	}
	if _, err := decodeKeyFile([]byte("short")); err == nil {
		t.Fatal("garbage must be rejected")
	}
	raw := bytes.Repeat([]byte{7}, 32)
	if got, err := decodeKeyFile(raw); err != nil || !bytes.Equal(got, raw) {
		t.Fatalf("raw 32-byte key from another platform must load: %v", err)
	}
	if _, err := loadKey(filepath.Join(home, "missing"), false); err == nil {
		t.Fatal("missing key without create must fail")
	}
}

// WSLENV lets `jt env cf -- wsl.exe -e <command>` carry the injected variables into Linux.
func TestForwardToChildExtendsWSLENV(t *testing.T) {
	env := forwardToChild([]string{"PATH=x", "WSLENV=FOO/p"}, []string{"A", "B"})
	if env[1] != "WSLENV=FOO/p:A:B" {
		t.Fatalf("existing WSLENV not extended: %v", env)
	}
	env = forwardToChild([]string{"PATH=x"}, []string{"A"})
	if len(env) != 2 || env[1] != "WSLENV=A" {
		t.Fatalf("WSLENV not added: %v", env)
	}
	if got := forwardToChild([]string{"PATH=x"}, nil); len(got) != 1 {
		t.Fatalf("no names must leave the environment alone: %v", got)
	}
}
