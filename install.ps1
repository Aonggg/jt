# Installs the latest jt release for Windows (jt.exe and jt-gui.exe) into
# %LOCALAPPDATA%\Programs\jt, adds that folder to the user PATH and creates a
# Start Menu shortcut for the GUI.
#
#   irm https://raw.githubusercontent.com/Aonggg/jt/main/install.ps1 | iex
#
# Environment overrides: JT_REPO (owner/name), JT_VERSION (tag, default latest),
# JT_INSTALL_DIR (folder for jt.exe), JT_BASE_URL (download the assets from
# another location, e.g. a mirror or a local build).
$ErrorActionPreference = 'Stop'

$repo = if ($env:JT_REPO) { $env:JT_REPO } else { 'Aonggg/jt' }
$version = if ($env:JT_VERSION) { $env:JT_VERSION } else { 'latest' }
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'amd64' }
$asset = "jt-windows-$arch.zip"
$base = if ($env:JT_BASE_URL) {
    $env:JT_BASE_URL.TrimEnd('/')
} elseif ($version -eq 'latest') {
    "https://github.com/$repo/releases/latest/download"
} else {
    "https://github.com/$repo/releases/download/$version"
}
$dest = if ($env:JT_INSTALL_DIR) { $env:JT_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\jt' }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("jt-install-" + [IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $zip = Join-Path $tmp $asset
    Invoke-WebRequest -Uri "$base/$asset" -OutFile $zip
    Invoke-WebRequest -Uri "$base/$asset.sha256" -OutFile "$zip.sha256"
    $expected = ((Get-Content "$zip.sha256" -Raw).Trim() -split '\s+')[0]
    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($expected -ne $actual) {
        throw "checksum mismatch for ${asset}: expected $expected, got $actual"
    }
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Copy-Item (Join-Path $tmp 'jt.exe') (Join-Path $dest 'jt.exe') -Force
    $gui = Join-Path $tmp 'jt-gui.exe'
    if (Test-Path $gui) {
        # A running jt-gui would keep its exe locked; stop it before overwriting.
        Get-Process jt-gui -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $dest 'jt-gui.exe') } | Stop-Process -Force
        Copy-Item $gui (Join-Path $dest 'jt-gui.exe') -Force
        $programs = [Environment]::GetFolderPath('Programs')
        $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $programs 'jt.lnk'))
        $shortcut.TargetPath = Join-Path $dest 'jt-gui.exe'
        $shortcut.WorkingDirectory = $dest
        $shortcut.Description = 'jt - secret references (Ctrl+Shift+J)'
        $shortcut.Save()
    }

    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (($userPath -split ';') -notcontains $dest) {
        [Environment]::SetEnvironmentVariable('Path', ($userPath.TrimEnd(';') + ";$dest"), 'User')
        $env:Path += ";$dest"
        Write-Host "added $dest to the user PATH; open a new terminal for other windows to see it"
    }
    Write-Host "installed $(& (Join-Path $dest 'jt.exe') version) to $dest\jt.exe"
    if (Test-Path (Join-Path $dest 'jt-gui.exe')) {
        Write-Host "installed jt-gui.exe; find 'jt' in the Start menu, then press Ctrl+Shift+J"
    }
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
