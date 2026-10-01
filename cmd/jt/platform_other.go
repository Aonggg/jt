//go:build !windows

package main

import (
	"os"
	"path/filepath"
)

// defaultBase is ~/.config/jt, the layout jt has always used on Linux and macOS.
// Inside WSL point the vault at the Windows copy with
// `jt init --vault /mnt/c/Users/<you>/AppData/Local/jt/vault` to share it.
func defaultBase() string {
	if xdg := os.Getenv("XDG_CONFIG_HOME"); xdg != "" {
		return filepath.Join(xdg, "jt")
	}
	home, err := os.UserHomeDir()
	if err != nil {
		home = "."
	}
	return filepath.Join(home, ".config", "jt")
}

// forwardToChild has nothing to do here: a Linux child inherits the environment as is.
func forwardToChild(environment []string, names []string) []string {
	return environment
}

const shellHint = "--exec sh -c '...'"
