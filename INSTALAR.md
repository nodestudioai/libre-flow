# Instalar Libre Flow con un asistente de código

Este archivo está escrito para Claude Code (o cualquier asistente que pueda correr comandos). Si sos una persona y preferís hacerlo a mano, los mismos pasos están en el [README](README.md#instalación-manual).

Objetivo: dejar Libre Flow instalado y abierto en esta computadora, y explicarle a la persona lo único que tiene que hacer ella: cargar su clave.

## Antes de empezar

- Solo funciona en Windows 10 u 11 de 64 bits. En otro sistema, avisá y no sigas.
- Todo se instala para el usuario actual. **No hace falta ni conviene abrir nada como administrador.**
- Usá PowerShell para los comandos.

## Pasos

### 1. .NET SDK 10

```powershell
dotnet --list-sdks
```

Si no aparece una línea que empiece con `10.`, instalalo:

```powershell
winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements
```

Windows va a mostrar un cartel pidiendo permiso; avisale a la persona que lo acepte. No hace falta reabrir la terminal: el instalador de Libre Flow encuentra .NET aunque todavía no figure en el PATH.

### 2. Descargar el código

Si la carpeta todavía no existe:

```powershell
git clone https://github.com/nodestudioai/libre-flow.git "$env:USERPROFILE\libre-flow"
```

Si ya existe, actualizala con `git -C "$env:USERPROFILE\libre-flow" pull`.

Sin Git: descargá `https://github.com/nodestudioai/libre-flow/archive/refs/heads/main.zip` y descomprimilo en esa misma carpeta.

### 3. Instalar

```powershell
powershell -ExecutionPolicy Bypass -File "$env:USERPROFILE\libre-flow\scripts\install.ps1"
```

Tarda entre uno y tres minutos. Compila la app, la copia a `%LOCALAPPDATA%\Programs\LibreFlow`, crea el acceso en el menú Inicio, la deja arrancando con Windows y la abre. Si ya había una versión instalada, la reemplaza y conserva los datos.

### 4. Comprobar

```powershell
Get-Process LibreFlow | Select-Object Id, StartTime
```

```powershell
Select-String -Path "$env:APPDATA\LibreFlow\logs\app.log" -Pattern 'ERROR' | Select-Object -Last 5
```

Tiene que haber un proceso `LibreFlow` y ningún error reciente en el registro. Si el panel no abre, puede faltar WebView2: `winget install --id Microsoft.EdgeWebView2Runtime -e`.

### 5. Explicarle a la persona lo que sigue

Decile, en pocas líneas:

1. Que entre a <https://console.groq.com/keys>, cree una cuenta gratis y toque **Create API Key**.
2. Que abra Libre Flow (ícono junto al reloj), vaya a **Ajustes**, pegue la clave y toque **Guardar clave**. Tiene que aparecer "Conexión correcta".
3. Que pruebe: mantener `Ctrl` + `Win`, decir una frase y soltar. Y escribir `ohla` seguido de espacio para ver el autocorrector.

## Lo que no hay que hacer

- **No pidas la clave ni la escribas vos.** La carga la persona en la app, que la guarda cifrada. No la pongas en archivos, variables de entorno ni comandos.
- No corras `tests/e2e`: toma el teclado durante medio minuto y escribe en la ventana que esté activa.
- No toques `%APPDATA%\LibreFlow`: ahí están el historial, el diccionario y los ajustes de la persona.

## Actualizar y desinstalar

Actualizar es repetir los pasos 2 y 3. Para desinstalar:

```powershell
powershell -ExecutionPolicy Bypass -File "$env:USERPROFILE\libre-flow\scripts\uninstall.ps1"
```

Conserva los datos. Solo si la persona pide borrarlos también, agregá `-BorrarDatos`.
