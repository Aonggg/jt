# Builds bin\jt.exe (Go) and bin\jt-gui.exe (C#). The GUI is compiled by the
# C# 5 compiler that every Windows 10/11 installation ships with .NET
# Framework, so no SDK is needed beyond Go.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force bin | Out-Null

go build -trimpath -ldflags '-s -w' -o bin\jt.exe .\cmd\jt
if ($LASTEXITCODE) { exit $LASTEXITCODE }

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$sources = Get-ChildItem gui\*.cs | ForEach-Object { $_.FullName }
& $csc /nologo /target:winexe /optimize+ /platform:anycpu /warnaserror+ /nowarn:1591 `
    /out:bin\jt-gui.exe /win32icon:gui\jt.ico /win32manifest:gui\app.manifest `
    /r:System.Web.Extensions.dll $sources
if ($LASTEXITCODE) { exit $LASTEXITCODE }

Write-Host 'built bin\jt.exe and bin\jt-gui.exe'
