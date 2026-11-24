param([string]$Exe, [string]$Data, [string]$Only = "")

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Security
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class K {
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern short VkKeyScan(char ch);
  public static void Down(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); }
  public static void Up(byte vk) { keybd_event(vk, 0, 2, UIntPtr.Zero); }
}
"@

$results = New-Object System.Collections.ArrayList
function Wait([int]$ms) {
  $until = [DateTime]::Now.AddMilliseconds($ms)
  while ([DateTime]::Now -lt $until) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 8 }
}
function Tap([byte]$vk) { [K]::Down($vk); Wait 12; [K]::Up($vk); Wait 22 }
function TypeKeys([string]$text) {
  foreach ($ch in $text.ToCharArray()) {
    if ($ch -eq "`n") { Tap 0x0D; continue }
    if ($ch -eq "`b") { Tap 0x08; continue }
    $scan = [K]::VkKeyScan($ch)
    $vk = [byte]($scan -band 0xFF)
    $shift = (($scan -shr 8) -band 1) -eq 1
    if ($shift) { [K]::Down(0x10); Wait 8 }
    Tap $vk
    if ($shift) { [K]::Up(0x10); Wait 8 }
  }
}
function Check([string]$name, [string]$expected, [string]$actual) {
  $ok = $expected -eq $actual
  [void]$results.Add([pscustomobject]@{ Test = $name; Ok = $ok; Expected = $expected; Actual = $actual })
}

# ---- settings for an isolated run against the local stand-in API ----
New-Item -ItemType Directory -Force $Data | Out-Null
$key = [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes('clave-de-prueba'), $null, 'CurrentUser'))
@{ provider = 'custom'; baseUrl = 'http://127.0.0.1:5601/v1'; asrModel = 'whisper-mock'; llmModel = 'mock-llm'; apiKeyProtected = $key
   language = 'auto'; cleanup = $true; toneByApp = $true; micDevice = ''; silenceThreshold = 0; sounds = $false; muteWhileDictating = $false
   pasteMethod = 'clipboard'; restoreClipboard = $true; trailingSpace = $true; showBar = $true; typingWpm = 40; keepAudioCount = 20
   autocorrectEnabled = $true; autocorrectTypos = $true; autocorrectAccents = $true; autocorrectCapitalize = $true; autocorrectOnEnter = $true
   autocorrectExcludedApps = @(); onboardingDone = $true } | ConvertTo-Json | Out-File "$Data\settings.json" -Encoding utf8

$env:LIBREFLOW_DATA = $Data
$env:LIBREFLOW_ACCEPT_INJECTED = '1'
$app = Start-Process $Exe -ArgumentList '--minimized' -PassThru
$log = "$Data\logs\app.log"
$ready = $false
for ($i = 0; $i -lt 80; $i++) {
  Start-Sleep -Milliseconds 250
  if ((Test-Path $log) -and (Select-String -Path $log -Pattern 'Autocorrector listo' -Quiet)) { $ready = $true; break }
}
"app pid=$($app.Id) autocorrector listo=$ready"
if (-not $ready) { if (Test-Path $log) { Get-Content $log -Tail 20 }; Stop-Process -Id $app.Id -Force; exit 1 }

# ---- a throwaway window to type into ----
$form = New-Object System.Windows.Forms.Form
$form.Text = 'Libre Flow - prueba automatica'
$form.Size = New-Object System.Drawing.Size(520, 220)
$form.StartPosition = 'CenterScreen'
$form.TopMost = $true
$box = New-Object System.Windows.Forms.TextBox
$box.Multiline = $true; $box.Dock = 'Fill'; $box.Font = New-Object System.Drawing.Font('Segoe UI', 12)
$form.Controls.Add($box)
$form.Show(); $form.Activate(); $box.Focus() | Out-Null
Wait 400
if ([K]::GetForegroundWindow() -ne $form.Handle) {
  # Windows refuses focus to background processes unless a key was just pressed.
  Tap 0x12; [K]::SetForegroundWindow($form.Handle) | Out-Null; $form.Activate(); $box.Focus() | Out-Null; Wait 300
}
$hasFocus = [K]::GetForegroundWindow() -eq $form.Handle
"ventana de prueba en primer plano=$hasFocus"

function Fresh { $box.Clear(); $box.Focus() | Out-Null; Tap 0x23; Wait 250 }   # End key resets what the app tracked
function Guard { return ([K]::GetForegroundWindow() -eq $form.Handle) }

try {
  if (-not $hasFocus) { throw 'No se pudo poner la ventana de prueba en primer plano' }
  $before = [System.Windows.Forms.Clipboard]::GetText()

  if ($Only -eq '' -or $Only -eq 'ac') {
    Fresh; TypeKeys 'ohla qeu tal tambien '; Wait 250
    Check 'typos y tilde al apretar espacio' 'hola que tal también ' $box.Text

    Fresh; TypeKeys 'hol acomo estas '; Wait 250
    Check 'espacio mal puesto' 'hola como estas ' $box.Text

    Fresh; TypeKeys "grcias `b"; Wait 250
    Check 'backspace deshace la correccion' 'grcias ' $box.Text

    Fresh; TypeKeys "necesito el archvo`n"; Wait 300
    Check 'corrige al apretar Enter' "necesito el archivo`r`n" $box.Text

    Fresh; TypeKeys "bien. ohla "; Wait 250
    Check 'mayuscula despues de punto' 'bien. Hola ' $box.Text

    Fresh; TypeKeys 'clave123 npm git jajaja '; Wait 250
    Check 'no toca claves, comandos ni risas' 'clave123 npm git jajaja ' $box.Text

    Fresh; TypeKeys 'escribo rapido sin parar y qeu todo salga bein aunque tipee muy rapdio '; Wait 300
    Check 'tipeo rapido sin mezclar letras' 'escribo rápido sin parar y que todo salga bien aunque tipee muy rápido ' $box.Text
  }

  if (($Only -eq '' -or $Only -eq 'voz') -and (Guard)) {
    Fresh
    [K]::Down(0xA2); Wait 15; [K]::Down(0x5B); Wait 1400; [K]::Up(0x5B); Wait 15; [K]::Up(0xA2)
    Wait 3500
    Check 'mantener Ctrl+Win: dicta y pega' 'Esta es una prueba de dictado con Vercel. ' $box.Text
    Check 'Ctrl+Win no abre el menu Inicio' 'True' ([string](Guard))

    Fresh
    [K]::Down(0xA2); [K]::Down(0x5B); Wait 90; [K]::Up(0x5B); [K]::Up(0xA2); Wait 140
    [K]::Down(0xA2); [K]::Down(0x5B); Wait 90; [K]::Up(0x5B); [K]::Up(0xA2)
    Wait 1500
    Check 'manos libres sigue grabando sin teclas' '' $box.Text
    [K]::Down(0xA2); [K]::Down(0x5B); Wait 90; [K]::Up(0x5B); [K]::Up(0xA2)
    Wait 3500
    Check 'doble toque: manos libres dicta y pega' 'Esta es una prueba de dictado con Vercel. ' $box.Text

    Fresh
    [K]::Down(0xA2); [K]::Down(0x5B); Wait 90; [K]::Up(0x5B); [K]::Up(0xA2)
    Wait 2500
    Check 'un solo toque corto no escribe nada' '' $box.Text

    Fresh
    [K]::Down(0x10); [K]::Down(0x12); Wait 20; Tap 0x5A; [K]::Up(0x12); [K]::Up(0x10)
    Wait 1500
    Check 'Shift+Alt+Z pega el ultimo dictado' 'Esta es una prueba de dictado con Vercel.' $box.Text

    Check 'el portapapeles vuelve a lo anterior' $before ([System.Windows.Forms.Clipboard]::GetText())
  }
}
catch { "ERROR: $_" }
finally {
  foreach ($vk in 0xA2, 0x5B, 0x10, 0x12) { [K]::Up([byte]$vk) }
  $form.Close()
  Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
}

$results | ForEach-Object { "{0} {1}" -f $(if ($_.Ok) { 'PASA ' } else { 'FALLA' }), $_.Test; if (-not $_.Ok) { "       esperado: [$($_.Expected)]"; "       obtenido: [$($_.Actual)]" } }
"total: $(@($results | Where-Object Ok).Count) de $($results.Count)"
if (Test-Path $log) { "--- log ---"; Get-Content $log -Tail 12 }
