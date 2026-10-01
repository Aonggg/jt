package main

import (
	"crypto/rand"
	"encoding/base64"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
)

// keyMagic prefixes a master key file that holds a Windows DPAPI blob. The
// other file format is 32 raw bytes, used on Linux, macOS and inside WSL; a
// raw key file works on Windows too, so a key can be copied either way.
const keyMagic = "JTDPAPI1"

// loadKey returns the 32-byte master key, creating one (in the platform's
// file format) when create is set and no key file exists.
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
	return decodeKeyFile(data)
}

func writeKey(path string, raw []byte) error {
	data, err := encodeKeyFile(raw)
	if err != nil {
		return err
	}
	return atomicWrite(path, data)
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
		fmt.Println(keyImportHint)
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
