# Prueba de punta a punta

Levanta la app real contra un servidor local que imita la API (no sale audio a internet),
abre una ventana de prueba y simula teclas: autocorrector, Ctrl+Win, doble toque, pegado.

**Toma el foco del teclado durante unos 40 segundos.** No escribir mientras corre.

```powershell
# 1. servidor de prueba (dejar corriendo en otra terminal)
python tests\e2e\mock_api.py $env:TEMP\ow-e2e\mock.log

# 2. la prueba (usa una carpeta de datos aparte; no toca tus datos reales)
dotnet build src\LibreFlow
powershell -ExecutionPolicy Bypass -File tests\e2e\e2e.ps1 `
  -Exe "$PWD\src\LibreFlow\bin\Debug\net10.0-windows\win-x64\LibreFlow.exe" `
  -Data "$env:TEMP\ow-e2e"
```

Para que la prueba de dictado reemplace "bercel" por "Vercel", la base de datos de esa carpeta
necesita esa palabra en el diccionario (agregarla desde el panel de una corrida previa).
