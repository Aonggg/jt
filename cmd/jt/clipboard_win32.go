//go:build windows

package main

import (
	"errors"
	"os/exec"
	"runtime"
	"syscall"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"
)

// Tests swap these for an in-memory clipboard; the defaults talk to Win32.
var (
	readClipboard           = win32ReadClipboard
	writeClipboard          = win32WriteClipboard
	clipboardHistoryEnabled = win32ClipboardHistoryEnabled
	clearClipboardHistory   = win32ClearClipboardHistory
)

const (
	cfUnicodeText = 13
	gmemMoveable  = 0x0002
	// Windows 10 clipboard history and cloud clipboard skip content carrying this format.
	excludeFromMonitorsFormat = "ExcludeClipboardContentFromMonitorProcessing"
)

var (
	user32                         = windows.NewLazySystemDLL("user32.dll")
	kernel32                       = windows.NewLazySystemDLL("kernel32.dll")
	procOpenClipboard              = user32.NewProc("OpenClipboard")
	procCloseClipboard             = user32.NewProc("CloseClipboard")
	procEmptyClipboard             = user32.NewProc("EmptyClipboard")
	procGetClipboardData           = user32.NewProc("GetClipboardData")
	procSetClipboardData           = user32.NewProc("SetClipboardData")
	procIsClipboardFormatAvailable = user32.NewProc("IsClipboardFormatAvailable")
	procRegisterClipboardFormatW   = user32.NewProc("RegisterClipboardFormatW")
	procGlobalAlloc                = kernel32.NewProc("GlobalAlloc")
	procGlobalFree                 = kernel32.NewProc("GlobalFree")
	procGlobalLock                 = kernel32.NewProc("GlobalLock")
	procGlobalUnlock               = kernel32.NewProc("GlobalUnlock")
	procGlobalSize                 = kernel32.NewProc("GlobalSize")
)

// openClipboard retries briefly: another application may hold the clipboard.
func openClipboard() error {
	for range 10 {
		if r, _, _ := procOpenClipboard.Call(0); r != 0 {
			return nil
		}
		time.Sleep(20 * time.Millisecond)
	}
	return errors.New("clipboard is in use by another application")
}

func win32ReadClipboard() (string, error) {
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()
	if r, _, _ := procIsClipboardFormatAvailable.Call(cfUnicodeText); r == 0 {
		return "", errors.New("clipboard does not contain text")
	}
	if err := openClipboard(); err != nil {
		return "", err
	}
	defer procCloseClipboard.Call()
	h, _, _ := procGetClipboardData.Call(cfUnicodeText)
	if h == 0 {
		return "", errors.New("clipboard does not contain text")
	}
	p, _, _ := procGlobalLock.Call(h)
	if p == 0 {
		return "", errors.New("cannot read clipboard memory")
	}
	defer procGlobalUnlock.Call(h)
	size, _, _ := procGlobalSize.Call(h)
	// UTF16ToString copies up to the first NUL, so the string outlives the unlock.
	return syscall.UTF16ToString(unsafe.Slice((*uint16)(globalPointer(p)), size/2)), nil
}

// globalPointer turns a GlobalLock result into a pointer. The block belongs to
// Windows, not the Go heap, so vet's uintptr-to-Pointer rule does not apply.
func globalPointer(p uintptr) unsafe.Pointer {
	return *(*unsafe.Pointer)(unsafe.Pointer(&p))
}

// win32WriteClipboard replaces the clipboard with text. sensitive content is
// marked so clipboard history and cloud sync do not keep a copy.
func win32WriteClipboard(text string, sensitive bool) error {
	u, err := syscall.UTF16FromString(text)
	if err != nil {
		return err
	}
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()
	if err := openClipboard(); err != nil {
		return err
	}
	defer procCloseClipboard.Call()
	if r, _, _ := procEmptyClipboard.Call(); r == 0 {
		return errors.New("cannot clear the clipboard")
	}
	if err := setClipboardData(cfUnicodeText, unsafe.Pointer(&u[0]), uintptr(len(u)*2)); err != nil {
		return err
	}
	if !sensitive {
		return nil
	}
	name, err := syscall.UTF16PtrFromString(excludeFromMonitorsFormat)
	if err != nil {
		return err
	}
	format, _, _ := procRegisterClipboardFormatW.Call(uintptr(unsafe.Pointer(name)))
	if format == 0 {
		return errors.New("cannot register the clipboard history exclusion format")
	}
	var zero uint32
	return setClipboardData(format, unsafe.Pointer(&zero), unsafe.Sizeof(zero))
}

// setClipboardData copies size bytes into movable global memory and hands it to the clipboard.
func setClipboardData(format uintptr, data unsafe.Pointer, size uintptr) error {
	h, _, _ := procGlobalAlloc.Call(gmemMoveable, size)
	if h == 0 {
		return errors.New("cannot allocate clipboard memory")
	}
	p, _, _ := procGlobalLock.Call(h)
	if p == 0 {
		procGlobalFree.Call(h)
		return errors.New("cannot lock clipboard memory")
	}
	copy(unsafe.Slice((*byte)(globalPointer(p)), size), unsafe.Slice((*byte)(data), size))
	procGlobalUnlock.Call(h)
	if r, _, _ := procSetClipboardData.Call(format, h); r == 0 {
		procGlobalFree.Call(h)
		return errors.New("cannot write to the clipboard")
	}
	return nil // The clipboard owns h now.
}

// win32ClipboardHistoryEnabled reports the Win+V clipboard history setting; Windows 10 defaults to off.
func win32ClipboardHistoryEnabled() bool {
	k, err := registry.OpenKey(registry.CURRENT_USER, `Software\Microsoft\Clipboard`, registry.QUERY_VALUE)
	if err != nil {
		return false
	}
	defer k.Close()
	v, _, err := k.GetIntegerValue("EnableClipboardHistory")
	return err == nil && v != 0
}

// win32ClearClipboardHistory removes every unpinned Win+V history entry. The
// WinRT clipboard API is reachable from Windows PowerShell without extra tools.
func win32ClearClipboardHistory() error {
	const script = `[Windows.ApplicationModel.DataTransfer.Clipboard,Windows.ApplicationModel.DataTransfer,ContentType=WindowsRuntime] | Out-Null; if (-not [Windows.ApplicationModel.DataTransfer.Clipboard]::ClearHistory()) { exit 1 }`
	out, err := exec.Command("powershell", "-NoProfile", "-NonInteractive", "-Command", script).CombinedOutput()
	if err != nil {
		return errors.New("clear clipboard history: " + string(out))
	}
	return nil
}
