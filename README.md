# ElysiumAudio

**ElysiumAudio** es un normalizador de audio basado en [EBU R128](https://tech.ebu.ch/publications/r128-es) para archivos WAV y FLAC. Calcula el LUFS integrado, el True Peak, el LRA y el PLR de cada archivo, aplica una normalización con limitador true-peak y exporta el resultado conservando los metadatos básicos cuando se solicita.

Escrito en C# con [Avalonia UI](https://avaloniaui.net/es/) (Windows), usando [NAudio](https://github.com/naudio/NAudio) y [ATL.Net](https://github.com/Zeugma440/atldotnet) para el procesamiento y análisis de audio.

## Características

- **Medición EBU R128**: LUFS integrado, Loudness Range (LRA, EBU Tech 3342) y Program Loudness Range (PLR).
- **True Peak**: medición y ceñido de picos verdaderos (`dBTP`).
- **Normalización con limitador**: busca el LUFS objetivo con un limitador true-peak iterativo para evitar clipping.
- **Procesado por lotes**: cola con análisis y normalización por lotes, vista previa por archivo en tiempo real.
- **Validación robusta**: mitigación de cabeceras RIFF/ATL, validación estructural de WAV y verificación byte-a-byte tras clonado de metadatos.
- **Interfaz clara**: grid de archivos con multi-selección, ficha de métricas, modos original/normalizado y estados visuales.
- **Topado de ganancia**: tope simétrico de ±30 dB por seguridad (no se rechazan archivos, se avisa si no se alcanza el objetivo).
- **Metadatos opcionales**: clonado básico de metadatos y portada cuando el usuario lo activa.
- **Internacionalización**: ES/EN.

## Requisitos para compilar

- Windows 10/11
- [.NET 10.0 SDK](https://dotnet.microsoft.com/es-es/download/dotnet/10.0)

Para **usar** la aplicación no hace falta el SDK: las binarias se publican *self-contained* (ver [Descarga](#descarga)).

## Compilación y ejecución

Clonar el repositorio:

```powershell
git clone https://github.com/simplan2/Elysium-Audio.git
cd "Elysium-Audio"
```

Restaurar y compilar en Debug:

```powershell
dotnet build -c Debug
```

Ejecutar:

```powershell
dotnet run --project .\ElysiumAudio\ElysiumAudio.csproj
```

Publicar la **versión portable** (carpeta con el ejecutable y todas las dependencias):

```powershell
dotnet publish .\ElysiumAudio\ElysiumAudio.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:DebugType=none -p:DebugSymbols=false -p:GenerateDocumentationFile=false -p:ErrorOnDuplicatePublishOutputFiles=false -o .\publish\win-x64
```

Comprimirla:

```powershell
Compress-Archive -Path .\publish\win-x64\* -DestinationPath .\ElysiumAudio-v1.0.0-win-x64.zip
```

La publicación *self-contained* incluye el runtime de .NET, así que la carpeta resultante funciona en cualquier Windows x64 sin instalar nada.

`PublishReadyToRun=true` precompila el IL a código nativo y ahorra unos **450 ms de arranque** (de ~1.090 ms a ~630 ms medido en caliente), a cambio de unos **17 MB más**. Ver [Tamaño de la distribución](#tamaño-de-la-distribución).

Para el **instalador** se usa [Inno Setup](https://jrsoftware.org/isinfo.php), que toma la carpeta publicada y la empaqueta. El script (`ElysiumAudio.iss`) queda fuera del repositorio por ser una herramienta de build externa.

## Descarga

La última versión disponible es **v1.0.0**.

| Versión | Formato | Archivo |
|---|---|---|
| Instalador | EXE (x64) | `ElysiumAudio-v1.0.0-win-x64-setup.exe` |
| Portable | ZIP (x64) | `ElysiumAudio-v1.0.0-win-x64.zip` |

- [Instalador (.exe)](https://github.com/simplan2/Elysium-Audio/releases/download/v1.0.0/ElysiumAudio-v1.0.0-win-x64-setup.exe) _(actualizar URL cuando se suba a Releases)_
- [Portable (.zip)](https://github.com/simplan2/Elysium-Audio/releases/download/v1.0.0/ElysiumAudio-v1.0.0-win-x64.zip) _(actualizar URL cuando se suba a Releases)_

El **instalador** añade una entrada en el menú Inicio, crea un acceso directo y permite desinstalar desde "Agregar o quitar programas". La **versión portable** no instala nada: extrae la carpeta en cualquier sitio y ejecuta `ElysiumAudio.exe`.

### Requisitos

- Windows 10/11 (x64)

Ambas variantes se publican *self-contained*, así que no necesitas instalar .NET para usarlas.

## Uso

1. Arrastra archivos WAV/FLAC a la zona de arrastre, o usa `Añadir archivos/carpeta`.
2. El análisis se lanza automáticamente o desde `Analizar`. Se muestran LUFS, Pico dBTP, LRA, PLR y duración.
3. Ajusta `Objetivo LUFS`, `Techo True Peak` y tiempos del limitador (Lookahead/Release) en Ajustes o en el panel lateral.
4. Pulsa `Normalizar` para procesar el lote. Los archivos salen en el directorio de salida elegido (o `_normalized` en el mismo directorio si coincide).
5. Usa `Supr` para quitar archivos seleccionados (multi-selección con Ctrl/Shift/Ctrl+A). `Vaciar lista` borra toda la cola.

## Tamaño de la distribución

Una publicación *self-contained* tal cual ronda los **133 MB**. Casi todo eso no es la aplicación: son el runtime de .NET y las bibliotecas nativas de Avalonia (Skia, HarfBuzz) y ATL (FLAC, Vorbis, Opus). Como referencia, un proyecto vacío de .NET publicado *self-contained* ya ocupa ~70 MB.

Lo que más pesaba eran los **símbolos de depuración** que aportan los paquetes nativos: `libSkiaSharp.pdb` (80 MB) y `libHarfBuzzSharp.pdb` (20 MB). El proyecto los elimina al publicar mediante el target `StripPublishDebugSymbols`, así que bajan ~100 MB sin tocar el código.

Para reducirlo más hay dos vías, con su contrapartida:

| Variante | Tamaño | Nota |
|---|---|---|
| *Framework-dependent* | 39 MB | Requiere [.NET Desktop Runtime 10](https://dotnet.microsoft.com/es-es/download/dotnet/10.0/runtime) instalado |
| *Self-contained* + ReadyToRun (la que se distribuye) | 133 MB | Sin dependencias, arranque ~630 ms |
| *Self-contained* sin ReadyToRun | 116 MB | Arranque ~1.090 ms |
| *Self-contained* + *trimming* + un solo exe | 24 MB | Exige revisar código reflection-dependent |

El *trimming* no se activa por defecto a propósito: `SettingsService` serializa con `System.Text.Json` dentro de un `try/catch`, así que si el recorte rompe la deserialización la app **no falla de forma visible**, sino que carga la configuración por defecto y pierde los ajustes del usuario. `ViewLocator` (resolución de vistas por reflexión) y la extensión `LocalizeExtension` tienen el mismo riesgo.

## Normas y estándares

El análisis y la normalización no son aproximaciones: implementan las especificaciones completas.

### ITU-R BS.1770-5 — medición de sonoridad

- **Doble compuerta (gating)**: absoluta a −70 LUFS y relativa a −10 LU respecto a la sonoridad media no filtrada.
- **Ventana de 400 ms con avance de 100 ms** (75 % de traslape).
- **Ponderación K** con los coeficientes del estándar y offset de −0,691.
- **Pesos de canal** según la tabla multicanal de la norma: 1,0 en L/R/C, 0 en LFE y 1,41 en surrounds. Soporta mono, estéreo, 5.1 y 7.1.

### True Peak — Anexo 2 de BS.1770-4/5

- **Sobremuestreo 4x** para detectar picos inter-sample, que no existen en la muestra digital pero aparecen al reconstruir la señal analógica.
- Se reporta en **dBTP** y es el techo que respeta el limitador.

### EBU Tech 3342 — Loudness Range (LRA)

- Sonoridad de **corto plazo con ventana de 3 s**.
- Doble compuerta: absoluta a **−70 LUFS** y relativa a **−20 LU** respecto a la media de corto plazo (más agresiva que la de −10 LU del LUFS integrado, según EBU Tech 3342 §3).
- **LRA = P95 − P10** de la distribución de corto plazo.
- Cuando no hay datos suficientes se marca como **no calculable**, no como 0, porque "no se puede calcular" y "cero de verdad" son cosas distintas.

### EBU R128 — normalización

- **Objetivo LUFS** configurable entre −24 y −6 LUFS. Por defecto **−14 LUFS**, el habitual en servicios de streaming.
- **Techo true-peak** configurable entre −3,0 y −0,1 dBTP. Por defecto **−1,0 dBTP**.
- **Limitador iterativo** con look-ahead (1–10 ms) y release (20–200 ms) para alcanzar el objetivo sin exceder el techo.
- **Tope de ganancia simétrico de ±30 dB** (ver la nota más abajo).

### Sobre el PLR

El **PLR** que muestra la tabla es la diferencia entre el pico verdadero y el LUFS integrado (`True Peak − LUFS`). Es una referencia rápida de cuánto pico tiene el material por encima de su sonoridad media. **No es** el "Average Dynamics (PLR)" de Youlean ni el PLR de mastering, que se calculan sobre otra distribución.

## Notas sobre el tope de ganancia

ElysiumAudio aplica un **tope simétrico de ±30 dB**. Cuando el archivo necesitaría más ganancia o más atenuación para llegar al objetivo, la ganancia se recorta a ±30 dB: el archivo se genera igualmente, pero queda por debajo (o por encima, en el caso de atenuación excesiva) del LUFS objetivo. Esto evita generar archivos con ruido amplificado de forma extrema y sigue el comportamiento de los normalizadores EBU R128 de referencia. Cuando el tope actúa, aparece un aviso en la fila indicando la loudness real alcanzada.

## Pruebas

El desarrollo de ElysiumAudio se validó con siete suites de pruebas independientes. Viven fuera del repositorio porque son andamiaje de desarrollo y no forman parte de la aplicación distribuible. **Las siete pasan** en la versión 1.0.0.

| Suite | Qué comprueba |
|---|---|
| `elylufs` | LUFS integrado y True Peak contra señales de referencia, incluidos casos límite de PLR |
| `elydev` | Conmutación Original/Normalizado, notificaciones `INotifyPropertyChanged` y estados degenerados del modelo |
| `lratest` | LRA con señal constante, con dos niveles separados, con rampa continua, en estéreo, el efecto de la compuerta relativa de −20 LU y los casos sin datos suficientes |
| `elyriff` | Chunks RIFF corruptos: detecciones de reservas masivas de memoria y de excepciones al escribir con ATL |
| `atlguard` | Validación y clonado de metadatos, sin regresiones ni reservas excesivas de memoria |
| `wavscan` | Barrido de archivos WAV y FLAC reales para descartar falsos positivos del validador |
| `lravm` | Tope de ganancia ±30 dB, selección múltiple con `Supr`, carga resiliente ante archivos corruptos y coherencia de LRA/PLR en el grid |

## Licencia

MIT. Consulta el archivo [LICENSE](LICENSE).

## Créditos

- [EBU R128](https://tech.ebu.ch/publications/r128-es) – especificación de loudness
- [Avalonia UI](https://avaloniaui.net/es/)
- [NAudio](https://github.com/naudio/NAudio)
- [ATL.Net](https://github.com/Zeugma440/atldotnet)
