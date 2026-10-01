//go:build !windows

package main

import (
	"bytes"
	"errors"
)

// encodeKeyFile stores the raw 32 bytes, the format jt has always used on
// Linux and macOS; the file is written with mode 0600 by atomicWrite.
func encodeKeyFile(raw []byte) ([]byte, error) {
	return raw, nil
}

// decodeKeyFile accepts the raw format and explains a Windows DPAPI file.
func decodeKeyFile(data []byte) ([]byte, error) {
	if len(data) == 32 {
		return data, nil
	}
	if bytes.HasPrefix(data, []byte(keyMagic)) {
		return nil, errors.New("this key file is protected by Windows DPAPI and cannot be read here; run `jt key export` on Windows, then `jt key import` in this environment")
	}
	return nil, errors.New("key file must contain 32 raw bytes")
}

const keyImportHint = "on another machine run: jt key import   (reads the clipboard, or pass the text as an argument)"
