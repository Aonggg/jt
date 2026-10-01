# Installs the latest jt release for Windows into %LOCALAPPDATA%\Programs\jt
# and adds that folder to the user PATH.
#
#   irm https://raw.githubusercontent.com/catoncat/jt/main/install.ps1 | iex
#
# Environment overrides: JT_REPO (owner/name), JT_VERSION (tag, default latest),
# JT_INSTALL_DIR (folder for jt.exe), JT_BASE_URL (download the assets from
# another location, e.g. a mirror or a local build).
$ErrorActionPreference = 'Stop'

$repo = if ($env:JT_REPO) { $env:JT_REPO } else { 'catoncat/jt' }
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

    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (($userPath -split ';') -notcontains $dest) {
        [Environment]::SetEnvironmentVariable('Path', ($userPath.TrimEnd(';') + ";$dest"), 'User')
        $env:Path += ";$dest"
        Write-Host "added $dest to the user PATH; open a new terminal for other windows to see it"
    }
    Write-Host "installed $(& (Join-Path $dest 'jt.exe') version) to $dest\jt.exe"
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
