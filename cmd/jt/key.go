//go:build windows

package main

import (
	"bytes"
	"crypto/rand"
	"encoding/base64"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"unsafe"

	"golang.org/x/sys/windows"
)

// keyMagic prefixes a master key file that holds a DPAPI blob. A file of
// exactly 32 raw bytes (the format used by jt on macOS and Linux) is read as
// well, so a key copied from another platform works without conversion.
const keyMagic = "JTDPAPI1"

// keyEntropy binds the DPAPI blob to jt: another program running as the same
// user cannot unprotect it by accident, only on purpose.
var keyEntropy = []byte("jt master key")

// loadKey returns the 32-byte master key, creating a DPAPI-protected one when
// create is set and no key file exists.
func loadKey(path string, create bool) ([]byte, error) {
	data, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) && create {
		raw := make([]byte, 32)
		if _, err := io.ReadFull(rand.Reader, raw); err != nil {
			return nil, err
		}
		if err := writeKey(path, raw); err != nil {
			return nil, err
		}
		return raw, nil
	}
	if err != nil {
		return nil, err
	}
	return decodeKey(data)
}

func writeKey(path string, raw []byte) error {
	blob, err := protectKey(raw)
	if err != nil {
		return fmt.Errorf("protect key: %w", err)
	}
	return atomicWrite(path, blob)
}

func decodeKey(data []byte) ([]byte, error) {
	if len(data) == 32 {
		return data, nil
	}
	if !bytes.HasPrefix(data, []byte(keyMagic)) {
		return nil, errors.New("key file must be a jt DPAPI key or contain 32 raw bytes")
	}
	raw, err := unprotectKey(data[len(keyMagic):])
	if err != nil {
		return nil, fmt.Errorf("unprotect key (created by another Windows user or machine?): %w", err)
	}
	if len(raw) != 32 {
		return nil, errors.New("protected key must contain 32 bytes")
	}
	return raw, nil
}

func protectKey(raw []byte) ([]byte, error) {
	in := windows.DataBlob{Size: uint32(len(raw)), Data: &raw[0]}
	entropy := windows.DataBlob{Size: uint32(len(keyEntropy)), Data: &keyEntropy[0]}
	var out windows.DataBlob
	if err := windows.CryptProtectData(&in, nil, &entropy, 0, nil, windows.CRYPTPROTECT_UI_FORBIDDEN, &out); err != nil {
		return nil, err
	}
	defer windows.LocalFree(windows.Handle(unsafe.Pointer(out.Data)))
	blob := make([]byte, 0, len(keyMagic)+int(out.Size))
	blob = append(blob, keyMagic...)
	return append(blob, unsafe.Slice(out.Data, out.Size)...), nil
}

func unprotectKey(blob []byte) ([]byte, error) {
	if len(blob) == 0 {
		return nil, errors.New("empty blob")
	}
	in := windows.DataBlob{Size: uint32(len(blob)), Data: &blob[0]}
	entropy := windows.DataBlob{Size: uint32(len(keyEntropy)), Data: &keyEntropy[0]}
	var out windows.DataBlob
	if err := windows.CryptUnprotectData(&in, nil, &entropy, 0, nil, windows.CRYPTPROTECT_UI_FORBIDDEN, &out); err != nil {
		return nil, err
	}
	defer windows.LocalFree(windows.Handle(unsafe.Pointer(out.Data)))
	return bytes.Clone(unsafe.Slice(out.Data, out.Size)), nil
}

// keyCommand moves the master key between machines. export never prints the
// key: it goes to the clipboard, excluded from clipboard history. import reads
// the base64 key from the argument or, without one, from the clipboard.
func keyCommand(args []string) error {
	if len(args) == 0 {
		return errors.New("key needs export or import [KEY]")
	}
	c, err := loadConfig()
	if err != nil {
		return err
	}
	switch args[0] {
	case "export":
		if len(args) != 1 {
			return errors.New("key export takes no arguments")
		}
		raw, err := loadKey(c.Key, false)
		if err != nil {
			return fmt.Errorf("load key: %w", err)
		}
		if err := writeClipboard(base64.StdEncoding.EncodeToString(raw), true); err != nil {
			return err
		}
		fmt.Println("master key copied to clipboard (excluded from clipboard history)")
		fmt.Println("on the other machine run: jt key import   (reads the clipboard)")
		fmt.Println("for jt on macOS/Linux:   echo '<paste>' | base64 -d > ~/.config/jt/key")
		return nil
	case "import":
		if len(args) > 2 {
			return errors.New("key import takes at most one argument")
		}
		if _, err := os.Stat(c.Key); err == nil {
			return fmt.Errorf("key already exists at %s; secrets encrypted with it would become unreadable. Move it away first", c.Key)
		}
		encoded := ""
		if len(args) == 2 {
			encoded = args[1]
		} else {
			encoded, err = readClipboard()
			if err != nil {
				return err
			}
		}
		raw, err := base64.StdEncoding.DecodeString(strings.TrimSpace(encoded))
		if err != nil || len(raw) != 32 {
			return errors.New("key must be the base64 form of 32 bytes, as produced by jt key export")
		}
		if err := os.MkdirAll(filepath.Dir(c.Key), 0700); err != nil {
			return err
		}
		if err := writeKey(c.Key, raw); err != nil {
			return err
		}
		fmt.Printf("imported master key to %s\n", c.Key)
		return nil
	default:
		return fmt.Errorf("unknown key command %q", args[0])
	}
}
