//go:build windows

package main

import (
	"bytes"
	"errors"
	"fmt"
	"unsafe"

	"golang.org/x/sys/windows"
)

// keyEntropy binds the DPAPI blob to jt: another program running as the same
// user cannot unprotect it by accident, only on purpose.
var keyEntropy = []byte("jt master key")

// encodeKeyFile wraps the raw key in a DPAPI blob: only this Windows user on
// this machine can read the file back.
func encodeKeyFile(raw []byte) ([]byte, error) {
	in := windows.DataBlob{Size: uint32(len(raw)), Data: &raw[0]}
	entropy := windows.DataBlob{Size: uint32(len(keyEntropy)), Data: &keyEntropy[0]}
	var out windows.DataBlob
	if err := windows.CryptProtectData(&in, nil, &entropy, 0, nil, windows.CRYPTPROTECT_UI_FORBIDDEN, &out); err != nil {
		return nil, fmt.Errorf("protect key: %w", err)
	}
	defer windows.LocalFree(windows.Handle(unsafe.Pointer(out.Data)))
	blob := make([]byte, 0, len(keyMagic)+int(out.Size))
	blob = append(blob, keyMagic...)
	return append(blob, unsafe.Slice(out.Data, out.Size)...), nil
}

// decodeKeyFile accepts a DPAPI key file or the 32 raw bytes other platforms use.
func decodeKeyFile(data []byte) ([]byte, error) {
	if len(data) == 32 {
		return data, nil
	}
	if !bytes.HasPrefix(data, []byte(keyMagic)) {
		return nil, errors.New("key file must be a jt DPAPI key or contain 32 raw bytes")
	}
	blob := data[len(keyMagic):]
	if len(blob) == 0 {
		return nil, errors.New("empty key blob")
	}
	in := windows.DataBlob{Size: uint32(len(blob)), Data: &blob[0]}
	entropy := windows.DataBlob{Size: uint32(len(keyEntropy)), Data: &keyEntropy[0]}
	var out windows.DataBlob
	if err := windows.CryptUnprotectData(&in, nil, &entropy, 0, nil, windows.CRYPTPROTECT_UI_FORBIDDEN, &out); err != nil {
		return nil, fmt.Errorf("unprotect key (created by another Windows user or machine?): %w", err)
	}
	defer windows.LocalFree(windows.Handle(unsafe.Pointer(out.Data)))
	raw := bytes.Clone(unsafe.Slice(out.Data, out.Size))
	if len(raw) != 32 {
		return nil, errors.New("protected key must contain 32 bytes")
	}
	return raw, nil
}

// keyImportHint names the platform in the key export instructions.
const keyImportHint = "on another Windows machine run: jt key import   (reads the clipboard)\nin WSL or on Linux/macOS:         jt key import   (jt there reads the Windows clipboard too, or paste the text)"
