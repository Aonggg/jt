//go:build windows

package main

import (
	"errors"
	"fmt"
	"os"
)

// grab is the clipboard entry point: copy a secret anywhere, run grab, and the
// clipboard holds a jt://secret/<id> reference to paste into an agent chat
// instead of the plaintext.
func grab(args []string) error {
	opts, err := parseWriteFlags(args, "--id", "--description", "--clear-history")
	if err != nil {
		return err
	}
	if len(opts.positional) != 1 || opts.positional[0] == "" {
		return errors.New("grab needs <name>")
	}
	c, err := loadConfig()
	if err != nil {
		return err
	}
	v, key, err := openVault(c, true)
	if err != nil {
		return err
	}
	value, err := readClipboard()
	if err != nil {
		return err
	}
	item, err := addEntry(c, v, key, opts.positional[0], trimLineEnd(value), opts)
	if err != nil {
		return err
	}
	ref := prefix + item.ID
	if opts.clearHistory {
		if err := clearClipboardHistory(); err != nil {
			fmt.Fprintln(os.Stderr, "jt:", err)
		}
	}
	if err := writeClipboard(ref, false); err != nil {
		return fmt.Errorf("stored %s but could not replace the clipboard: %w", ref, err)
	}
	fmt.Printf("grabbed %s %s\nreference copied to clipboard; the plaintext is no longer on it\n", item.Name, ref)
	if !opts.clearHistory && clipboardHistoryEnabled() {
		fmt.Fprintln(os.Stderr, "jt: Windows clipboard history (Win+V) is on and may still hold the plaintext; use --clear-history or clear it there")
	}
	return nil
}

// ref copies an existing secret's reference to the clipboard.
func ref(args []string) error {
	if len(args) != 1 {
		return errors.New("ref needs <name-or-ref>")
	}
	c, err := loadConfig()
	if err != nil {
		return err
	}
	v, _, err := openVault(c, false)
	if err != nil {
		return err
	}
	_, item, err := find(v, args[0])
	if err != nil {
		return err
	}
	if err := writeClipboard(prefix+item.ID, false); err != nil {
		return err
	}
	fmt.Printf("%s %s\nreference copied to clipboard\n", item.Name, prefix+item.ID)
	return nil
}

// copyValue puts the plaintext on the clipboard for a human to paste into a
// login form or settings page. It is the only way jt hands out a value
// outside a child process, and the content is excluded from clipboard history.
func copyValue(args []string) error {
	if len(args) != 1 {
		return errors.New("copy needs <name-or-ref>")
	}
	c, err := loadConfig()
	if err != nil {
		return err
	}
	v, key, err := openVault(c, false)
	if err != nil {
		return err
	}
	_, item, err := find(v, args[0])
	if err != nil {
		return err
	}
	value, err := open(key, item.Ciphertext)
	if err != nil {
		return err
	}
	if err := writeClipboard(value, true); err != nil {
		return err
	}
	fmt.Printf("copied %s (%s) to clipboard, excluded from clipboard history\n", item.Name, item.Preview)
	return nil
}
