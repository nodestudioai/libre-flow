# Compila Libre Flow en modo Release y lo instala para el usuario actual:
# carpeta de programa, acceso directo en el menú Inicio e inicio automático con Windows.
param([switch]$NoStart)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA 'Programs\LibreFlow'
$exe = Join-Path $dest 'LibreFlow.exe'

# Recién instalado, .NET todavía no figura en el PATH de esta ventana: se busca también en su carpeta.
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
$sdks = if (Test-Path $dotnet) { & $dotnet --list-sdks } else { @() }
if (-not ($sdks -match '^10\.')) {
    throw 'Falta .NET SDK 10. Instalalo con "winget install Microsoft.DotNet.SDK.10" y volvé a correr este instalador.'
}

Get-Process LibreFlow -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
Start-Sleep -Milliseconds 600

& $dotnet publish (Join-Path $repo 'src\LibreFlow') -c Release -o $dest --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'La compilación falló.' }

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Libre Flow.lnk'))
$link.TargetPath = $exe
$link.WorkingDirectory = $dest
$link.IconLocation = (Join-Path $dest 'Assets\app.ico')
$link.Description = 'Dictado por voz y autocorrector'
$link.Save()

Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'LibreFlow' -Value "`"$exe`" --minimized"

# Se abre a través del Explorador, no como hija de esta ventana. Si el instalador corre dentro de una
# app empaquetada (por ejemplo, la app de escritorio de un asistente), una hija directa heredaría su
# aislamiento y guardaría el diccionario, el historial y la clave donde un arranque normal no los ve.
if (-not $NoStart) { Start-Process explorer.exe "`"$exe`"" }
"Libre Flow instalado en $dest"
