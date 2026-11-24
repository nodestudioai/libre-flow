# Compila Libre Flow en modo Release y lo instala para el usuario actual:
# carpeta de programa, acceso directo en el menú Inicio e inicio automático con Windows.
param([switch]$NoStart)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA 'Programs\LibreFlow'
$exe = Join-Path $dest 'LibreFlow.exe'

Get-Process LibreFlow -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
Start-Sleep -Milliseconds 600

dotnet publish (Join-Path $repo 'src\LibreFlow') -c Release -o $dest --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'La compilación falló.' }

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Libre Flow.lnk'))
$link.TargetPath = $exe
$link.WorkingDirectory = $dest
$link.IconLocation = (Join-Path $dest 'Assets\app.ico')
$link.Description = 'Dictado por voz y autocorrector'
$link.Save()

Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'LibreFlow' -Value "`"$exe`" --minimized"

if (-not $NoStart) { Start-Process $exe }
"Libre Flow instalado en $dest"
