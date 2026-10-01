//go:build !windows

package main

import (
	"bytes"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestRawKeyFileAndDPAPIRefusal(t *testing.T) {
	home := t.TempDir()
	keyPath := filepath.Join(home, "key")
	first, err := loadKey(keyPath, true)
	if err != nil || len(first) != 32 {
		t.Fatalf("create key: %v", err)
	}
	data, err := os.ReadFile(keyPath)
	if err != nil || !bytes.Equal(data, first) {
		t.Fatalf("key file must hold the raw key: %v", err)
	}
	if info, _ := os.Stat(keyPath); info.Mode().Perm() != 0600 {
		t.Fatalf("key file mode %v, want 0600", info.Mode().Perm())
	}
	_, err = decodeKeyFile([]byte(keyMagic + "blob"))
	if err == nil || !strings.Contains(err.Error(), "DPAPI") {
		t.Fatalf("a Windows key file must be explained, got %v", err)
	}
}
