//go:build windows

package main

import (
	"os"
	"path/filepath"
	"strings"
)

// defaultBase is %LOCALAPPDATA%\jt: machine-local, so a roaming profile never carries the key.
func defaultBase() string {
	local := os.Getenv("LOCALAPPDATA")
	if local == "" {
		home, err := os.UserHomeDir()
		if err != nil {
			home = "."
		}
		local = filepath.Join(home, "AppData", "Local")
	}
	return filepath.Join(local, "jt")
}

// forwardToChild adds the injected variable names to WSLENV, so a child such
// as `wsl.exe -e <command>` carries them into the Linux side; other children
// ignore WSLENV.
func forwardToChild(environment []string, names []string) []string {
	if len(names) == 0 {
		return environment
	}
	value := strings.Join(names, ":")
	for i, kv := range environment {
		if strings.HasPrefix(strings.ToUpper(kv), "WSLENV=") {
			existing := kv[len("WSLENV="):]
			if existing != "" {
				value = existing + ":" + value
			}
			environment[i] = "WSLENV=" + value
			return environment
		}
	}
	return append(environment, "WSLENV="+value)
}

const shellHint = "--exec cmd /C \"...\" or --exec bash -c '...'"
