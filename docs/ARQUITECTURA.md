# Arquitectura

Libre Flow es un solo proceso nativo de Windows (.NET, WPF). Se eligió así, y no Electron, porque la app vive prendida escuchando el teclado: necesita acceso directo a las APIs de Windows y poca memoria en reposo.

## Partes

| Proyecto | Qué contiene |
|---|---|
| `LibreFlow.Core` | Motor del autocorrector, seguimiento de lo tipeado, base de datos, estadísticas, postproceso de texto. No depende de la interfaz. |
| `LibreFlow` | Gancho de teclado y mouse, atajos, grabación, cliente de transcripción, pegado, barrita flotante, ícono de bandeja y panel. |
| `LibreFlow.Tests` | Pruebas del motor, el seguimiento, la base de datos y las estadísticas. |

El panel es HTML, CSS y JavaScript locales (sin frameworks ni recursos externos) dentro de WebView2. Se comunica con la app por mensajes; `wwwroot/mock.js` da datos de muestra para abrirlo en un navegador común mientras se diseña.

## Dictado

1. `Ctrl` + `Win` inicia la grabación a 16 kHz mono.
2. Al soltar, el audio va a una API compatible con OpenAI (`/audio/transcriptions`), con las palabras del diccionario como pista.
3. El texto pasa por un modelo de lenguaje que quita muletillas, aplica las autocorrecciones dichas al hablar y ordena la puntuación. Si ese paso falla o cambia demasiado el texto, se usa la transcripción original.
4. Se aplican los reemplazos del diccionario y los atajos de texto.
5. El resultado se pega con el portapapeles y `Ctrl` + `V`, y el portapapeles vuelve a su contenido anterior.
6. Se guarda en el historial.

Proveedor, modelos y clave son configurables. La clave se guarda cifrada con DPAPI.

## Autocorrector

El gancho traduce cada tecla al carácter que escribe (incluidas las teclas muertas de las tildes) y mantiene una copia de la línea actual. Esa copia se descarta ante cualquier cosa que pueda mover el cursor: clic, flechas, atajos, cambio de ventana.

Al apretar espacio, o Enter si la línea tiene más de una palabra, evalúa el último bloque:

- **Válido** si lo acepta Hunspell (español o inglés), está en el diccionario personal o en las palabras aprendidas, o es voseo o jerga conocida. No se toca.
- **Si no**, busca candidatos a una edición de distancia comparando sin tildes, ponderados por tipo de error (transposición, tecla vecina, letra faltante o sobrante, confusiones como b/v o s/c/z) y por frecuencia de uso. También prueba partir la palabra, unirla con la anterior o correr el espacio.
- Solo corrige cuando un candidato le gana con claridad al resto. Ante la duda, no toca.

La corrección se envía dentro del mismo gancho (borrar, escribir, espacio) en un solo lote, para que nunca se mezcle con lo que la persona sigue tipeando.

Decisiones de diseño:

- Una corrección equivocada es peor que no corregir.
- Nunca se corrige nada con números o símbolos, ni un campo de contraseña.
- Lo tipeado no sale de la computadora.
- El gancho tiene que responder en milisegundos. Por eso el camino del teclado se ejecuta una vez al arrancar y su memoria se mantiene bloqueada en RAM: en equipos con poca memoria libre, Windows manda a disco los procesos ociosos y la primera tecla quedaba esperando.

## Datos

Todo vive en `%APPDATA%\LibreFlow`:

- `libreflow.db` (SQLite): historial, diccionario, atajos de texto, correcciones, palabras aprendidas.
- `settings.json`: ajustes y la clave cifrada.
- `audio/`: los últimos dictados, para reintentar si falla la red.
- `logs/app.log`: registro de eventos. No contiene texto dictado ni tipeado.

## Límites conocidos

- No actúa sobre ventanas abiertas como administrador.
- En barras de direcciones con autocompletado, una corrección puede desalinearse.
- En terminales y editores de código puede corregir comandos; para eso están el apagado rápido y la lista de programas excluidos.
