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

## Requisitos

- Windows 10/11
- [.NET 10.0 SDK](https://dotnet.microsoft.com/es-es/download/dotnet/10.0) (para compilar)

## Compilación y ejecución

Clonar el repositorio:

```powershell
git clone <url-del-repo>
cd "ElysiumAudio"
```

Restaurar y compilar en Debug:

```powershell
dotnet build -c Debug
```

Ejecutar:

```powershell
dotnet run --project .\ElysiumAudio\ElysiumAudio.csproj
```

Generar release:

```powershell
dotnet publish .\ElysiumAudio\ElysiumAudio.csproj -c Release -o .\publish
```

## Uso

1. Arrastra archivos WAV/FLAC a la zona de arrastre, o usa `Añadir archivos/carpeta`.
2. El análisis se lanza automáticamente o desde `Analizar`. Se muestran LUFS, Pico dBTP, LRA, PLR y duración.
3. Ajusta `Objetivo LUFS`, `Techo True Peak` y tiempos del limitador (Lookahead/Release) en Ajustes o en el panel lateral.
4. Pulsa `Normalizar` para procesar el lote. Los archivos salen en el directorio de salida elegido (o `_normalized` en el mismo directorio si coincide).
5. Usa `Supr` para quitar archivos seleccionados (multi-selección con Ctrl/Shift/Ctrl+A). `Vaciar lista` borra toda la cola.

## Notas sobre el tope de ganancia

ElysiumAudio aplica un **tope simétrico de ±30 dB**. Cuando el archivo necesitaría más ganancia o más atenuación para llegar al objetivo, la ganancia se recorta a ±30 dB: el archivo se genera igualmente, pero queda por debajo (o por encima, en el caso de atenuación excesiva) del LUFS objetivo. Esto evita generar archivos con ruido amplificado de forma extrema y sigue el comportamiento de los normalizadores EBU R128 de referencia. Cuando el tope actúa, aparece un aviso en la fila indicando la loudness real alcanzada.

## Pruebas

El proyecto incluye varias suites de validación (`lravm`, `lratest`, `elydev`, `elylufs`, `elyriff`, `wavscan`, `atlguard`) que pueden ejecutarse desde sus respectivos proyectos en `C:\Users\Juan\AppData\Local\Temp\opencode\` (entorno de desarrollo). Todas pasan tras los cambios realizados.

## Licencia

Consulta el archivo `LICENSE` si existe en el repositorio.

## Créditos

- [EBU R128](https://tech.ebu.ch/publications/r128-es) – especificación de loudness
- [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen)
- [Avalonia UI](https://avaloniaui.net/es/)
- [NAudio](https://github.com/naudio/NAudio)
- [ATL.Net](https://github.com/Zeugma440/atldotnet)
