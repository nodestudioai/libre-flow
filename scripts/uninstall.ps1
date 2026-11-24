# Quita Libre Flow del usuario actual. Conserva tus datos salvo que agregues -BorrarDatos.
param([switch]$BorrarDatos)

$program = Join-Path $env:LOCALAPPDATA 'Programs\LibreFlow'
$exe = Join-Path $program 'LibreFlow.exe'

Get-Process LibreFlow -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
Start-Sleep -Milliseconds 600

Remove-Item $program -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Libre Flow.lnk') -Force -ErrorAction SilentlyContinue
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'LibreFlow' -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'LibreFlow') -Recurse -Force -ErrorAction SilentlyContinue

$data = Join-Path $env:APPDATA 'LibreFlow'
if ($BorrarDatos) {
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    'Libre Flow desinstalado. También se borraron el historial, el diccionario y los ajustes.'
} else {
    "Libre Flow desinstalado. Tus datos siguen en $data"
}
