//go:build !windows

package main

import (
	"encoding/base64"
	"errors"
	"os/exec"
	"strings"
)

// Inside WSL the Windows clipboard is reached through powershell.exe, so grab,
// ref, copy and key import/export work the same as on the Windows side,
// including the clipboard-history exclusion for sensitive text. Elsewhere the
// usual Wayland, X11 and macOS tools are used.
var (
	readClipboard           = otherReadClipboard
	writeClipboard          = otherWriteClipboard
	clipboardHistoryEnabled = otherClipboardHistoryEnabled
	clearClipboardHistory   = otherClearClipboardHistory
)

const winrtClipboard = "[Windows.ApplicationModel.DataTransfer.Clipboard,Windows.ApplicationModel.DataTransfer,ContentType=WindowsRuntime] | Out-Null; "

func havePowerShell() bool {
	_, err := exec.LookPath("powershell.exe")
	return err == nil
}

// powershell runs a Windows PowerShell command with UTF-8 output and returns stdout.
func powershell(command string) (string, error) {
	out, err := exec.Command("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", "[Console]::OutputEncoding=[Text.Encoding]::UTF8; "+command).Output()
	if err != nil {
		var exitErr *exec.ExitError
		if errors.As(err, &exitErr) && len(exitErr.Stderr) > 0 {
			return "", errors.New(strings.TrimSpace(string(exitErr.Stderr)))
		}
		return "", err
	}
	return string(out), nil
}

func otherReadClipboard() (string, error) {
	if havePowerShell() {
		out, err := powershell("Get-Clipboard -Raw")
		if err != nil {
			return "", err
		}
		return strings.TrimSuffix(strings.TrimSuffix(out, "\n"), "\r"), nil
	}
	for _, candidate := range [][]string{{"wl-paste", "--no-newline"}, {"xclip", "-selection", "clipboard", "-o"}, {"pbpaste"}} {
		if _, err := exec.LookPath(candidate[0]); err == nil {
			out, err := exec.Command(candidate[0], candidate[1:]...).Output()
			if err != nil {
				return "", err
			}
			return string(out), nil
		}
	}
	return "", errors.New("no clipboard tool found (powershell.exe in WSL, wl-paste, xclip or pbpaste)")
}

// otherWriteClipboard passes the text base64-encoded so no quoting or code
// page can alter it; sensitive text is kept out of Windows clipboard history.
func otherWriteClipboard(text string, sensitive bool) error {
	if havePowerShell() {
		encoded := base64.StdEncoding.EncodeToString([]byte(text))
		script := winrtClipboard +
			"$text = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encoded + "')); " +
			"$package = New-Object Windows.ApplicationModel.DataTransfer.DataPackage; $package.SetText($text); " +
			"$options = New-Object Windows.ApplicationModel.DataTransfer.ClipboardContentOptions; " +
			"$options.IsAllowedInHistory = $" + boolWord(!sensitive) + "; $options.IsRoamable = $" + boolWord(!sensitive) + "; " +
			"if (-not [Windows.ApplicationModel.DataTransfer.Clipboard]::SetContentWithOptions($package, $options)) { exit 1 }; " +
			"[Windows.ApplicationModel.DataTransfer.Clipboard]::Flush()"
		_, err := powershell(script)
		return err
	}
	for _, candidate := range [][]string{{"wl-copy"}, {"xclip", "-selection", "clipboard"}, {"pbcopy"}} {
		if _, err := exec.LookPath(candidate[0]); err == nil {
			cmd := exec.Command(candidate[0], candidate[1:]...)
			cmd.Stdin = strings.NewReader(text)
			return cmd.Run()
		}
	}
	return errors.New("no clipboard tool found (powershell.exe in WSL, wl-copy, xclip or pbcopy)")
}

func boolWord(b bool) string {
	if b {
		return "true"
	}
	return "false"
}

func otherClipboardHistoryEnabled() bool {
	if !havePowerShell() {
		return false
	}
	out, err := powershell(winrtClipboard + "[Windows.ApplicationModel.DataTransfer.Clipboard]::IsHistoryEnabled()")
	return err == nil && strings.TrimSpace(out) == "True"
}

func otherClearClipboardHistory() error {
	if !havePowerShell() {
		return errors.New("clipboard history exists only on Windows; nothing to clear here")
	}
	_, err := powershell(winrtClipboard + "if (-not [Windows.ApplicationModel.DataTransfer.Clipboard]::ClearHistory()) { exit 1 }")
	if err != nil {
		return errors.New("clear clipboard history: " + err.Error())
	}
	return nil
}
