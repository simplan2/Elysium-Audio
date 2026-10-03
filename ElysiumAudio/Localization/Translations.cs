using System.Collections.Generic;

namespace ElysiumAudio.Localization
{
    /// <summary>
    /// Diccionarios de traducción (español por defecto / inglés).
    /// Las claves son compartidas por ambos diccionarios; si una clave falta
    /// en el idioma activo, LocalizationManager devuelve la propia clave.
    /// </summary>
    public static class Translations
    {
        public static IReadOnlyDictionary<string, string> Spanish { get; } = new Dictionary<string, string>
        {
            // --- Ventana principal / barra de acciones ---
            ["Add"] = "Agregar",
            ["AddFiles"] = "Agregar archivos",
            ["AddFolder"] = "Agregar carpeta",
            ["Clear"] = "Vaciar lista",
            ["Reset"] = "Reiniciar estados",
            ["Tracks"] = "PISTAS",

            // --- Cabeceras del DataGrid ---
            ["GridState"] = "Estado",
            ["GridFileName"] = "Archivo",
            ["GridCodec"] = "Códec",
            ["GridDuration"] = "Duración",
            ["GridLufs"] = "LUFS",
            ["GridPeak"] = "Pico dBTP",
            ["GridNormLufs"] = "Norm LUFS",
            ["GridNormPeak"] = "Norm Pico dBTP",

            // --- Estado vacío de la cola ---
            ["DropHintTitle"] = "Suelta aquí archivos o carpetas WAV / FLAC",
            ["DropHintSubtitle"] = "O usa Agregar Archivos / Agregar Carpeta para llenar la cola",

            // --- Fila inferior: formato / directorio ---
            ["OutputFormat"] = "FORMATO DE SALIDA",
            ["OutputDirectory"] = "DIRECTORIO DE SALIDA",
            ["ExploreDirectory"] = "EXPLORAR DIRECTORIO",

            // --- Selector de función ---
            ["Analyze"] = "Analizar",
            ["Normalize"] = "Normalizar",

            // --- Cabeceras del panel derecho ---
            ["Dsp"] = "DSP",
            ["DspSuffix"] = " · Calibración de Loudness",
            ["Analysis"] = "Análisis",
            ["ReportSuffix"] = " · Informe de Loudness",

            // --- Sección Target Loudness ---
            ["TargetLufs"] = "LUFS OBJETIVO",
            ["Limiter"] = "LIMITADOR",
            ["TruePeakCeiling"] = "Techo de pico real",
            ["ReleaseTime"] = "Tiempo de liberación",
            ["LookAheadTime"] = "Tiempo de anticipación",
            ["RangeOf"] = "Rango de",
            ["To"] = "a",

            // --- Ejecución y seguridad ---
            ["ExecutionSafety"] = "EJECUCIÓN Y SEGURIDAD",
            ["PreserveMetadata"] = "Preservar metadatos básicos",

            // --- Resumen del lote ---
            ["BatchSummary"] = "RESUMEN DEL LOTE",
            ["Average"] = "PROMEDIO",
            ["AvgGain"] = "GANANCIA MEDIA",
            ["MaxPeak"] = "PICO MÁXIMO",
            ["Measured"] = "Medidos",
            ["Range"] = "Rango",
            ["MaxDeviation"] = "Desviación máxima",
            ["OnTarget"] = "En objetivo",
            ["BatchEmptyHint"] = "Ejecuta el análisis para ver los resultados del lote.",

            // --- Ficha del archivo ---
            ["SelectedFile"] = "ARCHIVO SELECCIONADO",
            ["Format"] = "Formato",
            ["SampleRate"] = "Frecuencia",
            ["Duration"] = "Duración",
            ["Channels"] = "Canales",
            ["Loudness"] = "LOUDNESS",
            ["Target"] = "OBJETIVO",
            ["TruePeak"] = "PICO REAL",
            ["Plr"] = "PLR",
            ["Lra"] = "LRA",
            ["AvgLra"] = "LRA MEDIO",
            ["Deviation"] = "DESVIACIÓN",
            ["Original"] = "Original",
            ["Normalized"] = "Normalizado",
            ["GainToTarget"] = "GANANCIA AL OBJETIVO",
            ["GainToTargetWithTarget"] = "Ganancia hacia {0}",
            ["NoSelectionHint"] = "Selecciona un archivo de la lista para ver su ficha.",

            // --- Botón principal ---
            ["RunBatchNormalization"] = "Ejecutar normalización por lotes",
            ["RunBatchAnalysis"] = "Ejecutar análisis por lotes",
            ["Processing"] = "Procesando…",

            // --- Modal de cancelación ---
            ["ModalNormalizingTitle"] = "Normalizando archivos de audio",
            ["ModalAnalyzingTitle"] = "Analizando audio",
            ["ModalWait"] = "Por favor espere...",
            ["ModalTime"] = "Tiempo",
            ["ModalCancel"] = "Cancelar",
            ["ModalCancelling"] = "Cancelando…",

            // --- Confirmar salida ---
            ["ConfirmExitTitle"] = "Cerrar Elysium Audio",
            ["ConfirmExitMessage"] = "Hay Procesos en curso.\n¿Deseas cancelar el proceso y salir?",
            ["ContinueProcessing"] = "Seguir procesando",
            ["CancelAndExit"] = "Cancelar y salir",

            // --- Formato de salida / estados de ganancia ---
            ["OutputFormatKeep"] = "Mantener formato original",
            ["StatusOnTarget"] = "En objetivo",
            ["StatusNeedsGain"] = "Necesita ganancia",
            ["StatusTooLoud"] = "Demasiado fuerte",
            ["Mono"] = "Mono",
            ["Stereo"] = "Stereo",

            // --- Presets ---
            ["PresetDefault"] = "Predeterminado",
            ["PresetCustom"] = "Personalizado",
            ["PresetPodcast"] = "Podcast / Narración",

            // --- Mensajes de estado global ---
            ["StatusEngineReady"] = "Motor listo. Núcleo optimizado para mastering bit-perfect.",
            ["ModeAnalyze"] = "Modo: Análisis de loudness.",
            ["ModeNormalize"] = "Modo: Normalización de audio.",
            ["QueueEmptyWarn"] = "Advertencia: La cola de archivos está vacía.",
            ["AllCompletedNormalize"] = "Todos los archivos ya están completados. No hay nada que procesar.",
            ["StartNormalize"] = "Iniciando normalización de {0} archivo(s)...",
            ["NormalizeDone"] = "Normalización completada: {0} archivo(s) procesado(s) exitosamente.",
            ["ProcessDone"] = "Proceso finalizado: {0} éxitos, {1} error(es).",
            ["CancelSummary"] = "Procesamiento cancelado: {0} completados, {1} cancelados, {2} error(es).",
            ["CancelNoResults"] = "Procesamiento cancelado por el usuario.",
            ["ProcessingFile"] = "Procesando {0}/{1}: {2}",
            ["FileDone"] = "[{0}/{1}] Completado: {2}",
            ["FileError"] = "Error en {0}: {1}",
            ["FileDoneClamped"] = "{1}/{2}: {0} normalizado con tope, quedó en {3:F1} LUFS en vez del objetivo de {4:F1}.",
            ["QueueCleared"] = "Cola de archivos limpiada.",
            ["QueueRemovedSelection"] = "{0} archivo(s) quitados de la lista. Los archivos no se han borrado del disco.",
            ["QueueRemovedBusySkipped"] = "{0} archivo(s) no se quitaron porque se están procesando: {1}",
            ["QueueRemovedPartial"] = "{0} quitado(s); {1} se dejaron por estar procesándose: {2}. Los archivos no se borran del disco.",
            ["QueueReset"] = "Estado de la cola restablecido. Todos los archivos esperan reprocesarse.",
            ["Cancelling"] = "Cancelando el procesamiento... Los archivos en curso se descartarán de forma segura.",
            ["OutputDirSet"] = "Directorio de salida: {0}",
            ["ExploreError"] = "Error al abrir el directorio: {0}",
            ["OutputDirInvalid"] = "El directorio de salida no es válido o no existe.",
            ["AllAnalyzed"] = "Todos los archivos ya están analizados. No hay nada que procesar.",
            ["StartAnalysis"] = "Iniciando análisis de {0} archivo(s)...",
            ["AnalysisDone"] = "Análisis completado: {0} archivo(s) medidos.",
            ["AnalysisDoneAll"] = "Análisis finalizado: {0} éxitos, {1} error(es).",
            ["AnalysisCancelSummary"] = "Análisis cancelado: {0} medidos, {1} cancelados, {2} error(es).",
            ["AnalysisCancelNoResults"] = "Análisis cancelado por el usuario.",
            ["AnalyzingFile"] = "Analizando {0}/{1}: {2}",
            ["FileAnalyzed"] = "[{0}/{1}] Analizado: {2}",
            ["AnalyzeFileError"] = "Error al analizar {0}: {1}",
            ["FilesAdded"] = "Se añadieron {0} archivo(s) a la cola de procesamiento.",
            ["FolderNoAudio"] = "La carpeta seleccionada no contiene archivos WAV o FLAC.",
            ["FolderAdded"] = "Se añadieron {0} archivo(s) a la cola desde: {1}",
            ["DropNoFiles"] = "No se encontraron archivos WAV / FLAC.",
            ["DropLoading"] = "Cargando {0} archivo(s)...",
            ["DropAdded"] = "Se añadieron {0} archivo(s) a la cola.",
            ["QueuePartialFailed"] = "Se añadieron {0} archivo(s) a la cola; {1} no se pudieron leer y se omitieron.",
            ["QueueAllFailed"] = "No se pudo leer ninguno de los {0} archivo(s). Ninguno se añadió a la cola.",
            ["DropAllInQueue"] = "Todos los archivos ya están en la cola.",

            // --- Títulos de pickers del sistema ---
            ["PickerFolderTitle"] = "Selecciona una carpeta con pistas de audio",
            ["PickerOutputTitle"] = "Selecciona el directorio de salida",
            ["PickerAudioTitle"] = "Selecciona tus pistas de audio FLAC / WAV",
            ["PickerAudioType"] = "Audio de Alta Fidelidad",

            // --- Utilidades no visibles en el panel ---
            ["FunctionTitleAnalyze"] = "Análisis de loudness",
            ["FunctionTitleNormalize"] = "Normalizar audio",
            ["FunctionDescAnalyze"] = "Mide loudness y pico verdadero. Nunca modifica los archivos originales.",
            ["FunctionDescNormalize"] = "Aplica normalización de loudness y limitador de pico real a los archivos de salida.",
            ["QueueCounters"] = "Completados: {0}  ·  En curso: {1}",

            // --- Modal de Ajustes ---
            ["Settings"] = "Ajustes",
            ["SettingsTitle"] = "Ajustes de Elysium Audio",
            ["Language"] = "Idioma",
            ["OutputFolder"] = "Carpeta de salida",
            ["ChooseFolder"] = "Elegir carpeta…",
            ["About"] = "Acerca de",
            ["AppVersion"] = "Versión",
            ["AppAuthor"] = "Autor",
            ["Close"] = "Cerrar",
        };

        public static IReadOnlyDictionary<string, string> English { get; } = new Dictionary<string, string>
        {
            ["Add"] = "Add",
            ["AddFiles"] = "Add files",
            ["AddFolder"] = "Add folder",
            ["Clear"] = "Empty list",
            ["Reset"] = "Reset states",
            ["Tracks"] = "TRACKS",

            ["GridState"] = "State",
            ["GridFileName"] = "File Name",
            ["GridCodec"] = "Codec",
            ["GridDuration"] = "Duration",
            ["GridLufs"] = "LUFS",
            ["GridPeak"] = "Peak dBTP",
            ["GridNormLufs"] = "Norm LUFS",
            ["GridNormPeak"] = "Norm Peak dBTP",

            ["DropHintTitle"] = "Drop WAV / FLAC files or folders here",
            ["DropHintSubtitle"] = "Or use Add Files / Add Folder to build the queue",

            ["OutputFormat"] = "OUTPUT FORMAT",
            ["OutputDirectory"] = "OUTPUT DIRECTORY",
            ["ExploreDirectory"] = "EXPLORE DIRECTORY",

            ["Analyze"] = "Analyze",
            ["Normalize"] = "Normalize",

            ["Dsp"] = "DSP",
            ["DspSuffix"] = " · Loudness Calibration",
            ["Analysis"] = "Analysis",
            ["ReportSuffix"] = " · Loudness Report",

            ["TargetLufs"] = "TARGET LUFS",
            ["Limiter"] = "LIMITER",
            ["TruePeakCeiling"] = "True Peak Ceiling",
            ["ReleaseTime"] = "Release Time",
            ["LookAheadTime"] = "Look-ahead Time",
            ["RangeOf"] = "Range of",
            ["To"] = "to",

            ["ExecutionSafety"] = "EXECUTION & SAFETY",
            ["PreserveMetadata"] = "Preserve Basic Metadata",

            ["BatchSummary"] = "BATCH SUMMARY",
            ["Average"] = "AVERAGE",
            ["AvgGain"] = "AVG GAIN",
            ["MaxPeak"] = "MAX PEAK",
            ["Measured"] = "Measured",
            ["Range"] = "Range",
            ["MaxDeviation"] = "Max deviation",
            ["OnTarget"] = "On target",
            ["BatchEmptyHint"] = "Run the analysis to see the batch results.",

            ["SelectedFile"] = "SELECTED FILE",
            ["Format"] = "Format",
            ["SampleRate"] = "Sample rate",
            ["Duration"] = "Duration",
            ["Channels"] = "Channels",
            ["Loudness"] = "LOUDNESS",
            ["Target"] = "TARGET",
            ["TruePeak"] = "TRUE PEAK",
            ["Plr"] = "PLR",
            ["Lra"] = "LRA",
            ["AvgLra"] = "AVERAGE LRA",
            ["Deviation"] = "DEVIATION",
            ["Original"] = "Original",
            ["Normalized"] = "Normalized",
            ["GainToTarget"] = "GAIN TO TARGET",
            ["GainToTargetWithTarget"] = "Gain toward {0}",
            ["NoSelectionHint"] = "Select a file from the list to see its details.",

            ["RunBatchNormalization"] = "Run Batch Normalization",
            ["RunBatchAnalysis"] = "Run Batch Analysis",
            ["Processing"] = "Processing…",

            ["ModalNormalizingTitle"] = "Normalizing audio files",
            ["ModalAnalyzingTitle"] = "Analyzing audio",
            ["ModalWait"] = "Please wait...",
            ["ModalTime"] = "Time",
            ["ModalCancel"] = "Cancel",
            ["ModalCancelling"] = "Cancelling…",

            ["ConfirmExitTitle"] = "Close Elysium Audio",
            ["ConfirmExitMessage"] = "There are processes in progress.\nDo you want to cancel the process and exit?",
            ["ContinueProcessing"] = "Keep processing",
            ["CancelAndExit"] = "Cancel and exit",

            ["OutputFormatKeep"] = "Keep original format",
            ["StatusOnTarget"] = "On target",
            ["StatusNeedsGain"] = "Needs gain",
            ["StatusTooLoud"] = "Too loud",
            ["Mono"] = "Mono",
            ["Stereo"] = "Stereo",

            ["PresetDefault"] = "Default",
            ["PresetCustom"] = "Custom",
            ["PresetPodcast"] = "Podcast / Narration",

            ["StatusEngineReady"] = "Engine ready. Core optimized for bit-perfect mastering.",
            ["ModeAnalyze"] = "Mode: Loudness analysis.",
            ["ModeNormalize"] = "Mode: Audio normalization.",
            ["QueueEmptyWarn"] = "Warning: The file queue is empty.",
            ["AllCompletedNormalize"] = "All files are already completed. Nothing left to process.",
            ["StartNormalize"] = "Starting normalization of {0} file(s)...",
            ["NormalizeDone"] = "Normalization completed: {0} file(s) processed successfully.",
            ["ProcessDone"] = "Process finished: {0} succeeded, {1} error(s).",
            ["CancelSummary"] = "Processing cancelled: {0} completed, {1} cancelled, {2} error(s).",
            ["CancelNoResults"] = "Processing cancelled by the user.",
            ["ProcessingFile"] = "Processing {0}/{1}: {2}",
            ["FileDone"] = "[{0}/{1}] Completed: {2}",
            ["FileError"] = "Error in {0}: {1}",
            ["FileDoneClamped"] = "{1}/{2}: {0} normalized with gain cap, landed at {3:F1} LUFS instead of the {4:F1} target.",
            ["QueueCleared"] = "File queue cleared.",
            ["QueueRemovedSelection"] = "{0} file(s) removed from the list. The files themselves were not deleted from disk.",
            ["QueueRemovedBusySkipped"] = "{0} file(s) were not removed because they are being processed: {1}",
            ["QueueRemovedPartial"] = "{0} removed; {1} left in place because they are being processed: {2}. Files are not deleted from disk.",
            ["QueueReset"] = "Queue state reset. All files are waiting to be reprocessed.",
            ["Cancelling"] = "Cancelling the process... In-progress files will be safely discarded.",
            ["OutputDirSet"] = "Output directory: {0}",
            ["ExploreError"] = "Error opening the directory: {0}",
            ["OutputDirInvalid"] = "The output directory is not valid or does not exist.",
            ["AllAnalyzed"] = "All files are already analyzed. Nothing left to process.",
            ["StartAnalysis"] = "Starting analysis of {0} file(s)...",
            ["AnalysisDone"] = "Analysis completed: {0} file(s) measured.",
            ["AnalysisDoneAll"] = "Analysis finished: {0} succeeded, {1} error(s).",
            ["AnalysisCancelSummary"] = "Analysis cancelled: {0} measured, {1} cancelled, {2} error(s).",
            ["AnalysisCancelNoResults"] = "Analysis cancelled by the user.",
            ["AnalyzingFile"] = "Analyzing {0}/{1}: {2}",
            ["FileAnalyzed"] = "[{0}/{1}] Analyzed: {2}",
            ["AnalyzeFileError"] = "Error analyzing {0}: {1}",
            ["FilesAdded"] = "Added {0} file(s) to the processing queue.",
            ["FolderNoAudio"] = "The selected folder has no WAV or FLAC files.",
            ["FolderAdded"] = "Added {0} file(s) to the queue from: {1}",
            ["DropNoFiles"] = "No WAV / FLAC files found.",
            ["DropLoading"] = "Loading {0} file(s)...",
            ["DropAdded"] = "Added {0} file(s) to the queue.",
            ["QueuePartialFailed"] = "Added {0} file(s) to the queue; {1} could not be read and were skipped.",
            ["QueueAllFailed"] = "None of the {0} file(s) could be read. Nothing was added to the queue.",
            ["DropAllInQueue"] = "All files are already in the queue.",

            ["PickerFolderTitle"] = "Select a folder with audio tracks",
            ["PickerOutputTitle"] = "Select the output directory",
            ["PickerAudioTitle"] = "Select your FLAC / WAV audio tracks",
            ["PickerAudioType"] = "High Fidelity Audio",

            ["FunctionTitleAnalyze"] = "Loudness Analysis",
            ["FunctionTitleNormalize"] = "Normalize Audio",
            ["FunctionDescAnalyze"] = "Measures loudness and true peak only. Original files are never modified.",
            ["FunctionDescNormalize"] = "Applies loudness normalization and true-peak limiting to the output files.",
            ["QueueCounters"] = "Completed: {0}  ·  In progress: {1}",

            // --- Modal de Ajustes ---
            ["Settings"] = "Settings",
            ["SettingsTitle"] = "Elysium Audio settings",
            ["Language"] = "Language",
            ["OutputFolder"] = "Output folder",
            ["ChooseFolder"] = "Choose folder…",
            ["About"] = "About",
            ["AppVersion"] = "Version",
            ["AppAuthor"] = "Author",
            ["Close"] = "Close",
        };
    }
}