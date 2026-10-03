using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElysiumAudio.Helpers;
using ElysiumAudio.Localization;
using ElysiumAudio.Models;
using ElysiumAudio.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using System.Windows.Input;

namespace ElysiumAudio.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {

        #region Fields
        private readonly AudioEngineService _audioEngine = new();
        private readonly ISettingsService _settingsService;
        private static readonly string[] AudioExtensions = { ".wav", ".flac" };

        // Límite de tareas simultáneas (semáforo) en el procesamiento por lotes.
        // Análisis puro: ligero en RAM y CPU-bound (SIMD), admite más concurrencia.
        // Normalización: la ruta en memoria reserva hasta ~500 MB por archivo en el
        // peor caso, por eso se limita a 2 para no disparar el consumo de RAM.
        private const int MaxConcurrentAnalyzeTasks = 4;
        private const int MaxConcurrentNormalizeTasks = 2;

        // Lote actual: se cancelan y esperan desde el cierre de la ventana (ver MainWindow.OnClosing).
        private CancellationTokenSource? _batchCts;
        private Task? _batchTask;
        #endregion

        private static string L(string key) => LocalizationManager.Instance.Get(key);

        #region Properties

        // 1. Enlazado al Slider LUFS y TextBox numérico de la interfaz
        public double MinTargetLufs { get; set; } = DefaultValues.MIN_TARGET_LUFS;
        public double MaxTargetLufs { get; set; } = DefaultValues.MAX_TARGET_LUFS;

        [ObservableProperty]
        public double _targetLufs = DefaultValues.DEFAULT_TARGET_LUFS;

        // 2. Enlazado al TextBox del techo de pico real
        public double MinTruePeakCeiling { get; set; } = DefaultValues.MIN_TRUE_PEAK_CEILING;
        public double MaxTruePeakCeiling { get; set; } = DefaultValues.MAX_TRUE_PEAK_CEILING;
        [ObservableProperty]
        public double _truePeakCeiling = DefaultValues.DEFAULT_TRUE_PEAK_CEILING;

        // 3. Tiempo de liberacion del limitador en milisegundos
        public double MinReleaseTimeMs { get; set; } = DefaultValues.MIN_RELEASE_TIME_MS;
        public double MaxReleaseTimeMs { get; set; } = DefaultValues.MAX_RELEASE_TIME_MS;

        [ObservableProperty]
        private double _releaseTimeMs = DefaultValues.DEFAULT_RELEASE_TIME_MS;

        // 4. Tiempo de anticipación del limitador en milisegundos
        public double MinLookAheadTimeMs { get; set; } = DefaultValues.MIN_LOOK_AHEAD_TIME_MS;
        public double MaxLookAheadTimeMs { get; set; } = DefaultValues.MAX_LOOK_AHEAD_TIME_MS;

        [ObservableProperty]
        private double _lookAheadTimeMs = DefaultValues.DEFAULT_LOOK_AHEAD_TIME_MS;

        private string _systemStatus = LocalizationManager.Instance.Get("StatusEngineReady");

        // Semántica de la barra de estado: true solo cuando el último lote terminó sin
        // errores (verde "success"). Cualquier otro mensaje restablece el color neutro.
        public string SystemStatus
        {
            get => _systemStatus;
            set
            {
                if (SetProperty(ref _systemStatus, value))
                {
                    IsStatusSuccess = false;
                }
            }
        }

        [ObservableProperty]
        private bool _isStatusSuccess;

        [ObservableProperty]
        private bool _isProcessing;

        // --- Estado del lote mostrado en el modal de cancelación ---
        // Contadores reales del lote (se actualizan en el hilo de la UI), archivo en
        // curso (su StatusMessage se ve en vivo) y tiempo transcurrido. La barra de
        // progreso es indeterminada: transmite actividad sin engañar con porcentajes
        // que el procesamiento por etapas no puede medir con precisión.
        [ObservableProperty]
        private int _completedCount;

        [ObservableProperty]
        private int _inProgressCount;

        [ObservableProperty]
        private string _elapsedText = "00:00";

        [ObservableProperty]
        private bool _isCancelling;

        // Cronómetro del lote (tick por segundo para ElapsedText).
        private DateTime _batchStartTime;
        private Avalonia.Threading.DispatcherTimer? _elapsedTimer;

        // Idioma activo: alimenta el selector ES/EN de la barra superior.
        [ObservableProperty]
        private bool _isEnglish = LocalizationManager.Instance.IsEnglish;

        // Solo progreso: completados y en curso. El contador de errores se omite a
        // propósito, porque un lote todavía en marcha está lleno de archivos que AÚN no
        // han fallado, y anticipar "Errores: 0" en un modal centrado transmite que va a
        // salir mal aunque el lote vaya perfecto. El error de un archivo concreto se ve
        // en su propia fila del grid, que es donde sí se puede actuar.
        public string QueueCountersText => string.Format(L("QueueCounters"), CompletedCount, InProgressCount);

        // Tarea del lote en curso y forma de cancelarlo limpiamente. Lo usa la ventana
        // cuando el usuario cierra la app durante el procesamiento: cancela, espera a que
        // las tareas suelten los temporales, y solo entonces permite cerrar.
        public Task? CurrentBatchTask => _batchTask;

        public void RequestCancellation() => _batchCts?.Cancel();

        // Propósito activo: Normalizar o Analizar. Cambiarlo restablece el estado de la
        // cola (ver OnAppModeChanged) para que los archivos puedan reprocesarse con la nueva función.
        [ObservableProperty]
        private AppMode _appMode = AppMode.Normalize;

        public bool IsNormalizeMode => AppMode == AppMode.Normalize;
        public bool IsAnalyzeMode => AppMode == AppMode.Analyze;

        // Visibilidad de las dos etiquetas del boton de lote.
        //
        // Antes cada TextBlock llevaba su propio IsVisible ligado solo al modo, asi que
        // al arrancar el lote se veian las dos a la vez: "Ejecutar normalizacion por
        // lotes Procesando." El boton crecia a lo ancho porque los dos TextBlock viven
        // en el mismo StackPanel horizontal.
        //
        // Ahora el nombre de la operacion se esconde mientras se procesa y solo queda
        // "Procesando", que es lo que el icono de play ya hacia (IsVisible="!IsProcessing").
        public bool ShowRunNormalizationLabel => IsNormalizeMode && !IsProcessing;
        public bool ShowRunAnalysisLabel => IsAnalyzeMode && !IsProcessing;

        partial void OnIsProcessingChanged(bool value)
        {
            OnPropertyChanged(nameof(ShowRunNormalizationLabel));
            OnPropertyChanged(nameof(ShowRunAnalysisLabel));
        }


        // Título y descripción de la función activa, mostrados en la cabecera del panel derecho.
        public string FunctionTitle => IsAnalyzeMode ? L("FunctionTitleAnalyze") : L("FunctionTitleNormalize");

        public string FunctionDescription => IsAnalyzeMode
            ? L("FunctionDescAnalyze")
            : L("FunctionDescNormalize");

        // Formato de salida para los archivos normalizados
        [ObservableProperty]
        private OutputFormat _outputFormat = OutputFormat.SameAsSource;

        // Colección de opciones de formato. Se recalcula en cada lectura para que el texto
// siga al idioma activo (el ListBox se refresca al notificar OutputFormatOptions).
        public string[] OutputFormatOptions => new[] { L("OutputFormatKeep"), "WAV", "FLAC" };

        private static string FormatToOptionText(OutputFormat format) => format switch
        {
            OutputFormat.Wav => "WAV",
            OutputFormat.Flac => "FLAC",
            _ => L("OutputFormatKeep")
        };

        private string _selectedFormat = "WAV";
        public string SelectedFormat
        {
            get => _selectedFormat;
            set
            {
                if (value == null) return;
                if (_selectedFormat != value)
                {
                    _selectedFormat = value;
                    OnPropertyChanged(nameof(SelectedFormat));
                    // Actualizar la propiedad OutputFormat según la selección del ComboBox
                    OutputFormat = value switch
                    {
                        "WAV" => OutputFormat.Wav,
                        "FLAC" => OutputFormat.Flac,
                        // Cualquier otro texto es la opción traducida de "mantener formato".
                        _ => OutputFormat.SameAsSource
                    };
                }

                IsFlyoutOpen = false;
            }
        }

        // Presets de normalización para plataformas de streaming
        public ObservableCollection<NormalizationPreset> Presets { get; } = new();

        // Flag interno: evita bucles al aplicar un preset (los handlers no reaccionan a Custom)
        private bool _isApplyingPreset;

        [ObservableProperty]
        private NormalizationPreset? _selectedPreset;

        // Directorio de salida donde se guardan los archivos normalizados
        [ObservableProperty]
        private string _outputDirectory = DefaultValues.GetDefaultOutputDirectory()
            ?? Environment.CurrentDirectory;

        // --- Info del producto (modal de Ajustes) ---
        // Viene de los metadatos del ensamblado definidos en el .csproj.
        public string Version => Services.AppInfo.Version;
        public string Author => Services.AppInfo.Author;
        public string Copyright => Services.AppInfo.Copyright;
        public string Company => Services.AppInfo.Company;

        public ObservableCollection<AudioFileModel> AudioFiles { get; set; } = new();

        // Archivo seleccionado en el grid; alimenta la ficha del panel de análisis.
        [ObservableProperty]
        private AudioFileModel? _selectedFile;

        // --- Resumen del lote (panel de análisis) ---
        public string SummaryTotalText => AudioFiles.Count.ToString();

        public bool HasAnyMeasurement => AudioFiles.Any(f => f.HasMeasurement);

        public bool HasSelectedFile => SelectedFile != null;

        public string SummaryMeasuredText => AudioFiles.Count(f => f.HasMeasurement).ToString();

        public string SummaryAverageText
        {
            get
            {
                var m = MeasuredLoudness();
                return m.Count == 0 ? "—" : $"{m.Average():F1} LUFS";
            }
        }

        public string SummaryRangeText
        {
            get
            {
                var m = MeasuredLoudness();
                return m.Count == 0 ? "—" : $"{m.Min():F1} … {m.Max():F1} LUFS";
            }
        }

        public string SummaryPeakText
        {
            get
            {
                var peaks = AudioFiles.Where(f => f.HasMeasurement).Select(f => f.MaxTruePeakLinear).ToList();
                if (peaks.Count == 0) return "—";
                float max = peaks.Max();
                return max > 0f ? $"{20f * MathF.Log10(max):F2} dBTP" : "-oo dBTP";
            }
        }

        // LRA medio del lote. Dice si el conjunto es homogéneo en rango o si unas
        // pistas están mucho más aplastadas que otras, que es un problema de
        // producción que el loudness por sí solo no enseña.
        // Los archivos sin datos suficientes (silencio, clips más cortos que 3 s) NO
        // cuentan: promediar su NaN como cero hundiría la media del lote.
        public string SummaryLraText
        {
            get
            {
                var lras = AudioFiles
                    .Where(f => f.HasMeasurement && !float.IsNaN(f.LoudnessRange))
                    .Select(f => (double)f.LoudnessRange)
                    .ToList();
                return lras.Count == 0 ? "—" : $"{lras.Average():F1} LU";
            }
        }

        public string SummaryTargetText => $"{TargetLufs:F1} LUFS";
        public string SummaryOnTargetText
        {
            get
            {
                int measured = AudioFiles.Count(f => f.HasMeasurement);
                if (measured == 0) return "—";
                int ok = AudioFiles.Count(f => f.HasMeasurement && Math.Abs(f.IntegratedLoudness - TargetLufs) <= 0.5);
                return $"{ok} / {measured}";
            }
        }

        // Mayor desviación absoluta entre la sonoridad final y el objetivo, ya
        // normalizados. Responde de un vistazo a la pregunta que importa tras un
        // lote: ¿el limitador dejó algún archivo fuera de sitio?
        public double SummaryMaxAbsDeviation
        {
            get
            {
                double max = double.NaN;
                foreach (var f in AudioFiles)
                {
                    double d = Math.Abs(f.NormalizedDeviation);
                    if (double.IsNaN(d)) continue;
                    if (double.IsNaN(max) || d > max) max = d;
                }
                return max;
            }
        }

        public string SummaryMaxDeviationText =>
            double.IsNaN(SummaryMaxAbsDeviation) ? "—" : $"{SummaryMaxAbsDeviation:F1} dB";

        public IBrush SummaryMaxDeviationBrush => AudioFileModel.DeviationBrush(SummaryMaxAbsDeviation);

        // --- Ficha del archivo seleccionado ---
        public string SelectedFileGainNeeded
        {
            get
            {
                if (SelectedFile == null || !SelectedFile.HasMeasurement) return "—";
                float gain = (float)TargetLufs - SelectedFile.IntegratedLoudness;
                return $"{(gain >= 0 ? "+" : "")}{gain:F1} dB";
            }
        }

        public string SelectedFileSampleRateText =>
            SelectedFile == null || SelectedFile.SampleRate == 0
                ? "—"
                : $"{SelectedFile.SampleRate / 1000.0:0.#} kHz";

        public string SelectedFileChannelsText => SelectedFile == null || SelectedFile.Channels == 0
            ? "—"
            : SelectedFile.Channels switch { 1 => L("Mono"), 2 => L("Stereo"), _ => $"{SelectedFile.Channels} ch" };

        // ── Ficha: qué juego de métricas muestra el conmutador ────────────
        // El estado vive en el archivo (AudioFileModel.ShowNormalized); aquí solo se
        // traducen sus números al formato de presentación. Todas devuelven "—" en
        // cuanto el conjunto pedido no existe todavía.
        private bool ViewingNormalized => SelectedFile?.ShowNormalized == true;

        // Habilita el conmutador Original/Normalizado: solo si este archivo ya fue
        // normalizado. Un archivo pendiente no tiene conmutador que marcar.
        public bool HasNormalizedSelectedFile => SelectedFile?.HasNormalizedResult == true;

        public string SelectedFileLoudnessValue =>
            SelectedFile?.HasDisplayMetrics != true ? "—" : $"{SelectedFile.DisplayLoudness:F1}";

        public string SelectedFileLoudnessUnit =>
            SelectedFile?.HasDisplayMetrics == true ? "LUFS" : "";

        // El pico lee DisplayPeakDb, igual que LUFS, PLR y LRA leen sus Display*.
        // Antes esta ruta elegia entre los strings Peak/NormalizedPeak mirando
        // ViewingNormalized, que es una segunda copia de la regla "original vs
        // normalizado" que el modelo ya posee via ShowNormalized. Con dos verdades,
        // cualquier cambio en el modelo dejaba al pico fuera.
        //
        // La unidad va dentro del valor, no en una cabecera: el grid ya dice "Pico
        // dBTP" y aqui la ficha muestra las cuatro magnitudes autonomas.
        //
        // F2 a proposito, y no el F1 de las otras tres: cerca de 0 dBTP un decimal
        // pierde informacion (0.22 -> 0.2), que es justo donde se decide si el pico
        // respeta el ceiling. El grid usa el string del motor, tambien con F2.
        //
        // NaN cubre dos situations: sin medir, y silencio digital (el modelo devuelve
        // NaN cuando MaxTruePeakLinear es 0). Los dos se muestran "—", igual que PLR
        // y LRA, en vez del "-oo" que imprimia el string del motor.
        public string SelectedFilePeakText
        {
            get
            {
                if (SelectedFile?.HasDisplayMetrics != true) return "—";
                double db = SelectedFile.DisplayPeakDb;
                if (double.IsNaN(db)) return "—";
                return $"{db:F2} dBTP";
            }
        }

        // PLR (Peak-to-Loudness Ratio): distancia entre el pico real y el loudness
        // integrado. Un master con PLR alto está muy comprimido (dinámica aplastada);
        // uno con PLR bajo conserva rango. No tiene sentido sin medición real ni sobre
        // silencio digital, por eso se muestra "—".
        public string SelectedFilePlrText =>
            SelectedFile == null || double.IsNaN(SelectedFile.DisplayPlr)
                ? "—"
                : $"{SelectedFile.DisplayPlr:F1} dB";

        // LRA (Loudness Range, EBU Tech 3342): recorrido entre el percentil 95 y el 10
        // de la sonoridad de corto plazo. Dice cuánta variación de nivel conserva la
        // pieza a lo largo del tiempo, que es justo lo que la compresión destruye: un
        // master muy limitado da valores bajos aunque su loudness sea perfecto.
        // Se muestra "—" cuando no hay datos suficientes, nunca 0 por defecto.
        public string SelectedFileLraText =>
            SelectedFile == null || double.IsNaN(SelectedFile.DisplayLra)
                ? "—"
                : $"{SelectedFile.DisplayLra:F1} LU";

        // ── Fila de relación con el objetivo ──────────────────────────────
        // Solo aparece en la vista original: el objetivo es un ajuste global, no una
        // medida del archivo, así que no comparte lista con Loudness/Pico/PLR. En la
        // vista normalizada lo relevante es cuánto se desvió el resultado.
        // Estas dos propiedades son a la vez la visibilidad de la fila de relación y
        // el estado activo del conmutador, así que no se duplican.
        public bool IsViewingOriginalMetrics => HasSelectedFile && !ViewingNormalized;

        public bool IsViewingNormalizedMetrics => HasSelectedFile && ViewingNormalized;

        // "Ganancia hacia -14.0 LUFS" en la vista original.
        public string SelectedFileGainLabelText => string.Format(L("GainToTargetWithTarget"), SummaryTargetText);

        // Desviación con signo tras normalizar. El signo importa: un sesgo siempre
        // positivo en todo el lote delata que el techo de pico está empujando la
        // loudness hacia arriba.
        public string SelectedFileDeviationText
        {
            get
            {
                double d = SelectedFile?.NormalizedDeviation ?? double.NaN;
                if (double.IsNaN(d)) return "—";
                return $"{(d >= 0 ? "+" : "")}{d:F1} dB";
            }
        }

        public bool HasFiles => AudioFiles.Count > 0;

        // Marco de ticks decorativos (estilo plugin VST) para las escalas de parámetros.
        public int[] Ticks17 { get; } = Enumerable.Range(0, 17).ToArray();

        // --- Métricas adicionales del panel de análisis ---
        public string SummaryAvgGainText
        {
            get
            {
                var gains = AudioFiles.Where(f => f.HasMeasurement)
                    .Select(f => (float)TargetLufs - f.IntegratedLoudness).ToList();
                if (gains.Count == 0) return "—";
                float avg = gains.Average();
                return $"{(avg >= 0 ? "+" : "")}{avg:F1} dB";
            }
        }

        // Estado de ganancia del archivo seleccionado respecto al target.
        public string SelectedFileGainStatus
        {
            get
            {
                if (SelectedFile == null || !SelectedFile.HasMeasurement) return "—";
                float gain = (float)TargetLufs - SelectedFile.IntegratedLoudness;
                if (Math.Abs(gain) <= 0.25) return L("StatusOnTarget");
                return gain > 0 ? L("StatusNeedsGain") : L("StatusTooLoud");
            }
        }

        public IBrush SelectedFileGainBrush => SelectedFileGainBrushFor(alpha: 255);

        public IBrush SelectedFileGainBgBrush => SelectedFileGainBrushFor(alpha: 45);

        private IBrush SelectedFileGainBrushFor(byte alpha)
        {
            if (SelectedFile == null || !SelectedFile.HasMeasurement)
                return Brushes.Transparent;

            float gain = (float)TargetLufs - SelectedFile.IntegratedLoudness;
            (byte r, byte g, byte b) color = Math.Abs(gain) <= 0.25
                ? ((byte)0x34, (byte)0xD3, (byte)0x99)   // On target (verde)
                : gain > 0
                    ? ((byte)0x1E, (byte)0xA8, (byte)0xFA) // Needs gain (skyblue)
                    : ((byte)0xB2, (byte)0x7C, (byte)0xE8); // Too loud (rosa)

            return new SolidColorBrush(Color.FromArgb(alpha, color.r, color.g, color.b));
        }

        private List<float> MeasuredLoudness() =>
            AudioFiles.Where(f => f.HasMeasurement).Select(f => f.IntegratedLoudness).ToList();

        // Recalcula las propiedades calculadas del resumen y la ficha.
        private void RefreshAnalysisSummary()
        {
            OnPropertyChanged(nameof(HasFiles));
            OnPropertyChanged(nameof(HasAnyMeasurement));
            OnPropertyChanged(nameof(SummaryTotalText));
            OnPropertyChanged(nameof(SummaryMeasuredText));
            OnPropertyChanged(nameof(SummaryAverageText));
            OnPropertyChanged(nameof(SummaryRangeText));
            OnPropertyChanged(nameof(SummaryPeakText));
            OnPropertyChanged(nameof(SummaryLraText));
            OnPropertyChanged(nameof(SummaryTargetText));
            OnPropertyChanged(nameof(SummaryOnTargetText));
            OnPropertyChanged(nameof(SummaryMaxDeviationText));
            OnPropertyChanged(nameof(SummaryMaxDeviationBrush));
            OnPropertyChanged(nameof(SummaryAvgGainText));
            OnPropertyChanged(nameof(SelectedFileGainNeeded));
            OnPropertyChanged(nameof(SelectedFileSampleRateText));
            OnPropertyChanged(nameof(SelectedFileChannelsText));
            OnPropertyChanged(nameof(SelectedFilePeakText));
            OnPropertyChanged(nameof(SelectedFilePlrText));
            OnPropertyChanged(nameof(SelectedFileLraText));
            OnPropertyChanged(nameof(SelectedFileDeviationText));
            OnPropertyChanged(nameof(HasNormalizedSelectedFile));
            OnPropertyChanged(nameof(IsViewingOriginalMetrics));
            OnPropertyChanged(nameof(IsViewingNormalizedMetrics));
            OnPropertyChanged(nameof(SelectedFileGainLabelText));
            OnPropertyChanged(nameof(SelectedFileLoudnessValue));
            OnPropertyChanged(nameof(SelectedFileLoudnessUnit));
            OnPropertyChanged(nameof(SelectedFileGainStatus));
            OnPropertyChanged(nameof(SelectedFileGainBrush));
            OnPropertyChanged(nameof(SelectedFileGainBgBrush));
        }

        // Al cambiar de idioma se reenlaza el texto de las propiedades calculadas
        // (los textos del XAML se refrescan solos vía LocalizeExtension).
        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            IsEnglish = LocalizationManager.Instance.IsEnglish;
            RefreshLocalizedTexts();
        }

        private void RefreshLocalizedTexts()
        {
            SelectedFormat = FormatToOptionText(OutputFormat);
            OnPropertyChanged(nameof(OutputFormatOptions));
            OnPropertyChanged(nameof(QueueCountersText));
            OnPropertyChanged(nameof(FunctionTitle));
            OnPropertyChanged(nameof(FunctionDescription));
            OnPropertyChanged(nameof(SelectedFileGainStatus));
            OnPropertyChanged(nameof(SelectedFileChannelsText));

            // Los presets traducidos siguen al idioma activo.
            foreach (var preset in Presets)
            {
                if (!string.IsNullOrEmpty(preset.NameKey))
                {
                    preset.Name = L(preset.NameKey);
                }
            }

            // El mensaje en reposo es estático: se vuelve a traducir. Los mensajes
            // dinámicos (archivo en curso, errores…) describen un estado ya pasado y
            // se dejan tal cual hasta que haya un estado nuevo.
            if (LocalizationManager.IsTextOfKey(SystemStatus, "StatusEngineReady"))
            {
                SystemStatus = L("StatusEngineReady");
            }
        }

        partial void OnSelectedFileChanged(AudioFileModel? value)
        {
            OnPropertyChanged(nameof(HasSelectedFile));
            OnPropertyChanged(nameof(SelectedFileGainNeeded));
            OnPropertyChanged(nameof(SelectedFileSampleRateText));
            OnPropertyChanged(nameof(SelectedFileChannelsText));
            OnPropertyChanged(nameof(SelectedFilePeakText));
            OnPropertyChanged(nameof(SelectedFilePlrText));
            OnPropertyChanged(nameof(SelectedFileLraText));
            OnPropertyChanged(nameof(SelectedFileDeviationText));
            OnPropertyChanged(nameof(HasNormalizedSelectedFile));
            OnPropertyChanged(nameof(IsViewingOriginalMetrics));
            OnPropertyChanged(nameof(IsViewingNormalizedMetrics));
            OnPropertyChanged(nameof(SelectedFileGainLabelText));
            OnPropertyChanged(nameof(SelectedFileLoudnessValue));
            OnPropertyChanged(nameof(SelectedFileLoudnessUnit));
            OnPropertyChanged(nameof(SelectedFileGainStatus));
            OnPropertyChanged(nameof(SelectedFileGainBrush));
            OnPropertyChanged(nameof(SelectedFileGainBgBrush));
        }

        [ObservableProperty]
        private bool _isFlyoutOpen;

        [ObservableProperty]
        private bool _isPresetsFlyoutOpen;
        #endregion

        #region constructors

        public MainWindowViewModel(ISettingsService settingsService)
        {
            _settingsService = settingsService;

            // Cargar las preferencias guardadas del usuario
            TargetLufs = _settingsService.Current.TargetLufs;
            TruePeakCeiling = _settingsService.Current.TruePeakCeiling;
            ReleaseTimeMs = _settingsService.Current.ReleaseTimeMs;
            LookAheadTimeMs = _settingsService.Current.LookAheadTimeMs;

            if (!string.IsNullOrWhiteSpace(_settingsService.Current.OutputDirectory) && Directory.Exists(_settingsService.Current.OutputDirectory))
            {
                OutputDirectory = _settingsService.Current.OutputDirectory;
            }

            OutputFormat = _settingsService.Current.OutputFormat;

            // Restaurar la función activa guardada (si el usuario cerró en modo Analyze,
            // la app vuelve a abrirse en modo Analyze).
            AppMode = _settingsService.Current.AppMode;

            // Clonado de metadatos al normalizar (checkbox "Preservar metadatos básicos").
            PreserveMetadata = _settingsService.Current.PreserveMetadata;

            // Cargar los presets predefinidos para plataformas de streaming
            LoadPresets();

            // Idioma: aplicar el guardado en los settings y refrescar el texto inicial.
            LocalizationManager.Instance.LanguageChanged += OnLanguageChanged;
            LocalizationManager.Instance.SetLanguage(_settingsService.Current.Language);
            IsEnglish = LocalizationManager.Instance.IsEnglish;
            SystemStatus = L("StatusEngineReady");

            // Reaccionar a cambios externos del modelo de configuración
            _settingsService.SettingsChanged += OnSettingsChanged;
        }

        private void LoadPresets()
        {
            // "Predeterminado" siempre usa los valores de fábrica (DefaultValues).
            // Al aplicarlo, los handlers lo devuelven a Custom al primer ajuste manual.
            Presets.Add(new NormalizationPreset
            {
                Name = L("PresetDefault"),
                NameKey = "PresetDefault",
                TargetLufs = DefaultValues.DEFAULT_TARGET_LUFS,
                TruePeakCeiling = DefaultValues.DEFAULT_TRUE_PEAK_CEILING,
                ReleaseTimeMs = DefaultValues.DEFAULT_RELEASE_TIME_MS,
                LookAheadTimeMs = DefaultValues.DEFAULT_LOOK_AHEAD_TIME_MS
            });

            Presets.Add(new NormalizationPreset
            {
                Name = L("PresetCustom"),
                NameKey = "PresetCustom",
                IsCustom = true,
                TargetLufs = TargetLufs,
                TruePeakCeiling = TruePeakCeiling,
                ReleaseTimeMs = ReleaseTimeMs,
                LookAheadTimeMs = LookAheadTimeMs
            });

            Presets.Add(new NormalizationPreset
            {
                Name = "Spotify",
                TargetLufs = -14.0,
                TruePeakCeiling = -1.0,
                ReleaseTimeMs = 50.0,
                LookAheadTimeMs = 5.0
            });

            Presets.Add(new NormalizationPreset
            {
                Name = "YouTube",
                TargetLufs = -14.0,
                TruePeakCeiling = -1.0,
                ReleaseTimeMs = 50.0,
                LookAheadTimeMs = 5.0
            });

            Presets.Add(new NormalizationPreset
            {
                Name = "Apple Music",
                TargetLufs = -16.0,
                TruePeakCeiling = -1.0,
                ReleaseTimeMs = 50.0,
                LookAheadTimeMs = 5.0
            });

            Presets.Add(new NormalizationPreset
            {
                Name = L("PresetPodcast"),
                NameKey = "PresetPodcast",
                TargetLufs = -16.0,
                TruePeakCeiling = -1.0,
                ReleaseTimeMs = 50.0,
                LookAheadTimeMs = 5.0
            });

            SelectedPreset = Presets.FirstOrDefault(p => p.IsCustom);
        }

        // Al elegir un preset distinto de Custom, se aplican sus valores a los controles.
        // El flag _isApplyingPreset evita que los handlers los devuelvan a Custom.
        partial void OnOutputFormatChanged(OutputFormat value)
        {
            // Sincroniza el texto mostrado en la UI cuando el formato cambia
            // (ya sea por selección del usuario o por carga del settings).
            SelectedFormat = FormatToOptionText(value);

            if (_settingsService.Current.OutputFormat != value)
            {
                _settingsService.Current.OutputFormat = value;
            }
        }

        // Al cambiar de función solo se actualiza el contexto de la interfaz.
        // La cola ya NO se restablece aquí: reprocesar los archivos es una decisión
        // explícita del usuario (botón Reset), así el estado "Completed" se respeta
        // al pasar entre Normalize y Analyze.
        partial void OnAppModeChanged(AppMode value)
        {
            OnPropertyChanged(nameof(IsNormalizeMode));
            OnPropertyChanged(nameof(IsAnalyzeMode));
            OnPropertyChanged(nameof(ShowRunNormalizationLabel));
            OnPropertyChanged(nameof(ShowRunAnalysisLabel));
            OnPropertyChanged(nameof(FunctionTitle));
            OnPropertyChanged(nameof(FunctionDescription));

            RefreshAnalysisSummary();

            // Persistir la función activa: si el usuario cierra la app con "Analizar"
            // seleccionado, al volver a abrir se restablece ese mismo modo.
            if (_settingsService.Current.AppMode != value)
            {
                _settingsService.Current.AppMode = value;
            }

            SystemStatus = IsAnalyzeMode
                ? L("ModeAnalyze")
                : L("ModeNormalize");
        }

        /// <summary>
        /// Clona los metadatos (tags + carátula) del original al archivo de salida
        /// al terminar cada normalización. Lo controla el checkbox "Preservar
        /// metadatos básicos" y se persiste con el resto de preferencias.
        /// </summary>
        [ObservableProperty]
        private bool _preserveMetadata = true;

        partial void OnPreserveMetadataChanged(bool value)
        {
            if (_settingsService.Current.PreserveMetadata != value)
            {
                _settingsService.Current.PreserveMetadata = value;
            }
        }

        // Devuelve cada archivo de la cola a su estado inicial para poder reprocesarlo.
        private void ResetAllFileStates()
        {
            foreach (var file in AudioFiles)
            {
                file.Status = AudioFileStatus.Pending;
                file.StatusMessage = "Pending";
            }
        }

        partial void OnSelectedPresetChanged(NormalizationPreset? value)
        {
            // Al elegir un preset se cierra el selector desplegable
            IsPresetsFlyoutOpen = false;

            if (value == null || value.IsCustom) return;

            _isApplyingPreset = true;
            try
            {
                TargetLufs = value.TargetLufs;
                TruePeakCeiling = value.TruePeakCeiling;
                ReleaseTimeMs = value.ReleaseTimeMs;
                LookAheadTimeMs = value.LookAheadTimeMs;
            }
            finally
            {
                _isApplyingPreset = false;
            }
        }

        // Si el usuario toca un control manualmente, el preset pasa a Custom
        // (los valores ya no coinciden con el preset predefinido).
        private void ReturnToCustomPreset()
        {
            if (_isApplyingPreset) return;

            var custom = Presets.FirstOrDefault(p => p.IsCustom);
            if (custom != null && SelectedPreset != custom)
            {
                SelectedPreset = custom;
            }
        }

        // Sincroniza las propiedades del ViewModel si el modelo de configuración cambia
        // por otra vía distinta a este ViewModel (ej. reset, otra ventana, etc.).
        // El guard (when) evita bucles: si el cambio proviene de aquí, el valor ya coincide.
        private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            var settings = _settingsService.Current;
            switch (e.PropertyName)
            {
                case nameof(Models.UserSettings.TargetLufs) when TargetLufs != settings.TargetLufs:
                    TargetLufs = settings.TargetLufs;
                    break;
                case nameof(Models.UserSettings.TruePeakCeiling) when TruePeakCeiling != settings.TruePeakCeiling:
                    TruePeakCeiling = settings.TruePeakCeiling;
                    break;
                case nameof(Models.UserSettings.ReleaseTimeMs) when ReleaseTimeMs != settings.ReleaseTimeMs:
                    ReleaseTimeMs = settings.ReleaseTimeMs;
                    break;
                case nameof(Models.UserSettings.LookAheadTimeMs) when LookAheadTimeMs != settings.LookAheadTimeMs:
                    LookAheadTimeMs = settings.LookAheadTimeMs;
                    break;
                case nameof(Models.UserSettings.OutputDirectory) when OutputDirectory != settings.OutputDirectory:
                    OutputDirectory = settings.OutputDirectory;
                    break;
                case nameof(Models.UserSettings.OutputFormat) when OutputFormat != settings.OutputFormat:
                    OutputFormat = settings.OutputFormat;
                    break;
                case nameof(Models.UserSettings.AppMode) when AppMode != settings.AppMode:
                    AppMode = settings.AppMode;
                    break;
                case nameof(Models.UserSettings.Language) when LocalizationManager.Instance.Language != settings.Language:
                    LocalizationManager.Instance.SetLanguage(settings.Language);
                    break;
            }
        }
        #endregion

        #region Commands
        // Comando para añadir archivos a la cola de procesamiento
        [RelayCommand]
        private async Task AddFile()
        {
            await OnAddFile();
        }

        // Comando para añadir todos los archivos de audio de una carpeta
        [RelayCommand]
        private async Task AddDirectory()
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                return;

            var topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
            if (topLevel == null) return;

            var options = new FolderPickerOpenOptions
            {
                Title = L("PickerFolderTitle"),
                AllowMultiple = false
            };

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
            var folder = folders.FirstOrDefault();
            if (folder == null) return;

            string dirPath = folder.Path.LocalPath;

            var files = ResolveAudioPaths(new[] { dirPath });

            if (files.Count == 0)
            {
                SystemStatus = L("FolderNoAudio");
                return;
            }

            int added = AddFilesToQueue(files, out int failed);
            if (failed == 0)
            {
                SystemStatus = string.Format(L("FolderAdded"), added, Path.GetFileName(dirPath));
            }
            else
            {
                ReportQueueResult(added, failed);
            }
        }

        // Comando para iniciar la normalización por lotes
        [RelayCommand]
        private async Task OnStartNormalizationAsync()
        {
            if (IsProcessing) return;

            if (IsAnalyzeMode)
            {
                await RunAnalysisOnlyAsync();
                return;
            }

            if (AudioFiles.Count == 0)
            {
                SystemStatus = L("QueueEmptyWarn");
                return;
            }

            ResetModalProgress();

            IsProcessing = true;

            // Solo se procesan los archivos que aún no están completados
            var filesToProcess = AudioFiles
                .Where(f => f.Status != AudioFileStatus.Completed)
                .ToList();

            int totalFiles = filesToProcess.Count;

            if (totalFiles == 0)
            {
                IsProcessing = false;
                ResetModalProgress();
                SystemStatus = L("AllCompletedNormalize");
                return;
            }

            StartElapsedTimer();

            SystemStatus = string.Format(L("StartNormalize"), totalFiles);

            var progress = new BatchProgress();
            using var cts = new CancellationTokenSource();
            _batchCts = cts;

            try
            {
                // Semáforo: hasta MaxConcurrentNormalizeTasks archivos se analizan y
                // renderizan a la vez; los demás esperan su turno. 2 máximo porque la
                // ruta en memoria reserva ~500 MB por archivo en el peor caso.
                using var semaphore = new SemaphoreSlim(MaxConcurrentNormalizeTasks);

                var tasks = new Task[totalFiles];
                for (int i = 0; i < tasks.Length; i++)
                {
                    tasks[i] = NormalizeFileAsync(filesToProcess[i], semaphore, totalFiles, progress, cts.Token);
                }
                _batchTask = Task.WhenAll(tasks);
                await _batchTask;
            }
            finally
            {
                StopElapsedTimer();
                _batchCts = null;
                _batchTask = null;
                cts.Dispose();
            }

            IsProcessing = false;
            ResetModalProgress();

            if (progress.Cancelled > 0)
            {
                SystemStatus = progress.Succeeded > 0
                    ? string.Format(L("CancelSummary"), progress.Succeeded, progress.Cancelled, progress.Errors)
                    : L("CancelNoResults");
                IsStatusSuccess = false;
            }
            else if (progress.Errors == 0)
            {
                SystemStatus = string.Format(L("NormalizeDone"), totalFiles);
                IsStatusSuccess = true;
            }
            else
            {
                SystemStatus = string.Format(L("ProcessDone"), progress.Succeeded, progress.Errors);
            }

            RefreshAnalysisSummary();
        }

        // Normaliza UN archivo completo (análisis → ganancia → renderizado → metadatos)
        // limitando la concurrencia con el semáforo. Tras cada await se retoma en el
        // hilo de la UI, así que las escrituras sobre AudioFileModel y SystemStatus son
        // seguras; solo los contadores del lote se incrementan con Interlocked.
        private async Task NormalizeFileAsync(AudioFileModel file, SemaphoreSlim semaphore, int totalFiles, BatchProgress progress, CancellationToken ct)
        {
            bool acquired = false;
            try
            {
                await semaphore.WaitAsync(ct);
                acquired = true;

                int position = Interlocked.Increment(ref progress.Processed);
                UpdateModalCounters(progress);
                SystemStatus = string.Format(L("ProcessingFile"), position, totalFiles, file.FileName);

                // PASO 1: Análisis
                file.Status = AudioFileStatus.Analyzing;
                file.StatusMessage = $"Analizando... (0%)";

                var analysis = await Task.Run(() => _audioEngine.AnalyzeAudioFile(file.FilePath, ct), ct);

                file.Peak = analysis.TruePeakDbFormatted;
                file.Loudness = analysis.LoudnessFormatted;
                file.IntegratedLoudness = analysis.IntegratedLoudness;
                file.MaxTruePeakLinear = analysis.MaxTruePeakLinear;
                file.LoudnessRange = analysis.LoudnessRange;
                file.SampleRate = analysis.SampleRate;
                file.Channels = analysis.Channels;
                file.HasMeasurement = true;
                file.StatusMessage = "Análisis completado (25%)";

                // PASO 2: Cálculo de ganancia
                file.Status = AudioFileStatus.Processing;
                file.StatusMessage = "Calculando ganancia... (25%)";

                float maxPeakLimitDb = (float)TruePeakCeiling;
                float requiredGainDb = _audioEngine.CalculateTargetGain(analysis.IntegratedLoudness, (float)TargetLufs);

                // PASO 2b: Tope de ganancia. No es un veto: el archivo se normaliza
                // igual, pero con la ganancia recortada a ±30 dB. Así un archivo en
                // silencio digital no se pierde, y sale por debajo del objetivo en
                // vez de con el ruido disparado. Cuando el tope actúa se avisa en la
                // fila y en SystemStatus, porque el objetivo ya no se cumple y el
                // usuario tiene que saberlo.
                float appliedGainDb = AudioEngineService.ClampSafeGain(requiredGainDb, out bool gainClamped);

                string inputDirectory = Path.GetDirectoryName(file.FilePath) ?? "";
                string filenameWithoutExt = Path.GetFileNameWithoutExtension(file.FilePath);
                string ext = Path.GetExtension(file.FilePath);

                // Formato de salida: si el usuario elige WAV o FLAC, se convierte,
                // si no, se conserva la extensión del archivo original.
                string outputExt = OutputFormat switch
                {
                    OutputFormat.Wav => ".wav",
                    OutputFormat.Flac => ".flac",
                    _ => ext
                };

                // Si el directorio de salida coincide con el de entrada, agregamos "_normalized"
                // para no pisar el archivo original; si son distintos, conservamos el nombre.
                string outputDir = OutputDirectory ?? inputDirectory;
                bool sameDir = string.Equals(
                    Path.GetFullPath(outputDir).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(inputDirectory).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);

                string outputFileName = sameDir
                    ? $"{filenameWithoutExt}_normalized{outputExt}"
                    : $"{filenameWithoutExt}{outputExt}";

                string outputPath = OutputPathHelper.GetUniqueOutputPath(
                    Path.Combine(outputDir, outputFileName));

                file.StatusMessage = "Renderizando audio... (50%)";

                // Valores del limitador desde las preferencias del usuario
                float releaseMs = (float)ReleaseTimeMs;
                float lookAheadMs = (float)LookAheadTimeMs;

                // PASO 3: Renderizado. La convergencia hacia el LUFS objetivo la gestiona
                // el propio servicio, re-renderizando SIEMPRE desde el original para no
                // acumular pérdida generacional.
                var normalizeResult = await Task.Run(() =>
                {
                    return _audioEngine.ApplyNormalizationWithLimiter(
                        file.FilePath,
                        outputPath,
                        appliedGainDb,
                        maxPeakLimitDb,
                        releaseMs: releaseMs,
                        lookAheadMs: lookAheadMs,
                        targetLufs: (float)TargetLufs,
                        ct: ct
                    );
                }, ct);

                file.NormalizedLoudness = normalizeResult.LoudnessFormatted;
                file.NormalizedPeak = normalizeResult.TruePeakDbFormatted;
                file.NormalizedLoudnessDb = normalizeResult.IntegratedLoudness;
                file.NormalizedLoudnessRange = normalizeResult.LoudnessRange;
                file.NormalizedPeakDb = normalizeResult.MaxTruePeakLinear > 0f
                    ? 20.0 * Math.Log10(normalizeResult.MaxTruePeakLinear)
                    : (double)TruePeakCeiling - 10.0;
                file.TargetLufs = (float)TargetLufs;
                file.CeilingDb = (float)TruePeakCeiling;
                file.StatusMessage = "Renderizado completado (90%)";

                // El aviso del tope va aquí, ya con la loudness real de la salida: el
                // dato útil para el usuario no es la ganancia pedida sino cuánto se
                // quedó corto. Las StatusMessage de fila van en español fijo en todo
                // el archivo; solo SystemStatus pasa por traducciones.
                if (gainClamped)
                {
                    file.StatusMessage =
                        $"Normalizado con tope: {appliedGainDb:F1} dB en vez de {requiredGainDb:F1} dB " +
                        $"({normalizeResult.IntegratedLoudness:F1} LUFS en vez de {TargetLufs:F1})";
                }

                // PASO 4: Metadatos (después del renderizado, que re-escribe outputPath
                // y sobrescribiría el archivo etiquetado). Solo si el usuario dejó
                // marcado "Preservar metadatos básicos".
                if (PreserveMetadata)
                {
                    file.Status = AudioFileStatus.Tagging;
                    file.StatusMessage = "Aplicando metadatos... (98%)";
                    await Task.Run(() => MetadataService.CloneMetadataAndCover(file.FilePath, outputPath));
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MetadataService] Clonado omitido por preferencia del usuario: {file.FileName}");
                }

                // Completado. Con tope sigue siendo un éxito: el archivo existe y es
                // utilizable, solo que por debajo del objetivo, y el aviso ya quedó
                // puesto arriba. SystemStatus también menciona el tope para que el
                // resumen del lote no diga "FileDone" y esconda que no se llegó.
                file.Status = AudioFileStatus.Completed;
                file.StatusMessage = gainClamped
                    ? file.StatusMessage + " (100%)"
                    : "Completado (100%)";
                Interlocked.Increment(ref progress.Succeeded);
                UpdateModalCounters(progress);
                SystemStatus = gainClamped
                    ? string.Format(L("FileDoneClamped"), position, totalFiles, file.FileName,
                        normalizeResult.IntegratedLoudness, TargetLufs)
                    : string.Format(L("FileDone"), position, totalFiles, file.FileName);
            }
            catch (OperationCanceledException)
            {
                // El usuario cerró la app o canceló el lote: se deja el archivo en Pending
                // para que pueda reprocesarse, sin marcar error (no es un fallo).
                Interlocked.Increment(ref progress.Cancelled);
                UpdateModalCounters(progress);
                file.Status = AudioFileStatus.Pending;
                file.StatusMessage = "Cancelado por el usuario";
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref progress.Errors);
                UpdateModalCounters(progress);
                file.Status = AudioFileStatus.Error;
                file.StatusMessage = $"Error: {ex.Message}";
                SystemStatus = string.Format(L("FileError"), file.FileName, ex.Message);
            }
            finally
            {
                if (acquired) semaphore.Release();
            }
        }

        [RelayCommand]
        private void OnClearList()
        {
            AudioFiles.Clear();
            SystemStatus = L("QueueCleared");
            RefreshAnalysisSummary();
        }

        /// <summary>
        /// Quita de la cola los archivos seleccionados. Solo saca las filas de la tabla:
        /// no toca el disco, así que el archivo sigue donde estaba y se puede volver a
        /// arrastrar si la eliminación fue un error.
        ///
        /// Se llama desde la tecla Supr de la vista. Si la selección incluye un archivo
        /// que ya se está procesando, ese archivo no se quita: la tarea en curso lo
        /// tiene abierto y borrarlo de la lista dejaría un temporal huérfano y el
        /// resumen del lote descuadrado. Se avisa y el resto sí sale.
        /// </summary>
        public void RemoveSelectedFiles(IEnumerable<AudioFileModel> selected)
        {
            if (selected is null) return;

            var toRemove = selected
                .Where(f => f is not null)
                .Distinct()
                .ToList();

            if (toRemove.Count == 0) return;

            var busy = toRemove
                .Where(IsFileBusy)
                .ToList();
            var removable = toRemove.Except(busy).ToList();

            foreach (var file in removable)
            {
                AudioFiles.Remove(file);
            }

            // SelectedFile es la ficha del panel de análisis. Si el archivo que se
            // estaba viendo se va, hay que soltarlo o la ficha se queda enseñando
            // métricas de una fila que ya no está.
            if (SelectedFile is { } current && removable.Contains(current))
            {
                SelectedFile = null;
            }

            // Una sola linea para los dos hechos. Si se mandan mensajes seguidos, el
            // segundo pisa al primero y el usuario solo ve uno de los dos.
            string primero = busy.Count == 1 ? busy[0].FileName : string.Join(", ", busy.Select(f => f.FileName));

            if (removable.Count > 0 && busy.Count > 0)
            {
                SystemStatus = string.Format(L("QueueRemovedPartial"), removable.Count, busy.Count, primero);
            }
            else if (removable.Count > 0)
            {
                SystemStatus = string.Format(L("QueueRemovedSelection"), removable.Count);
            }
            else if (busy.Count > 0)
            {
                SystemStatus = string.Format(L("QueueRemovedBusySkipped"), busy.Count, primero);
            }

            RefreshAnalysisSummary();
        }

        // Un archivo está ocupado si su estado es uno de los intermedios del lote.
        // Pending y Completed no bloquean: no hay ninguna tarea viva sobre ellos.
        private static bool IsFileBusy(AudioFileModel file) =>
            file.Status is AudioFileStatus.Analyzing
                or AudioFileStatus.Processing
                or AudioFileStatus.Tagging;

        // Restablece manualmente el estado de la cola para poder reprocesar todo:
        // los archivos vuelven a Pending y se limpian las columnas de salida de la
        // normalización (las mediciones LUFS/Peak del análisis se conservan).
        [RelayCommand]
        private void ResetQueue()
        {
            ResetAllFileStates();

            foreach (var file in AudioFiles)
            {
                file.NormalizedLoudness = "—";
                file.NormalizedPeak = "—";
                file.NormalizedLoudnessDb = double.NaN;
                file.NormalizedPeakDb = double.NaN;
                file.NormalizedLoudnessRange = float.NaN;
                file.TargetLufs = float.NaN;
                file.CeilingDb = float.NaN;
            }

            RefreshAnalysisSummary();
            SystemStatus = L("QueueReset");
        }

        // Cancela cooperativamente el lote en curso desde el modal de progreso.
        // Las tareas activas sueltan sus temporales y no copian nada parcial al destino.
        [RelayCommand]
        private void CancelProcessing()
        {
            if (IsCancelling) return;
            IsCancelling = true;
            IsStatusSuccess = false;
            SystemStatus = L("Cancelling");
            RequestCancellation();
        }

        // Comando para elegir el directorio de salida de los archivos normalizados
        [RelayCommand]
        private async Task SelectOutputDirectory()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
                if (topLevel == null) return;

                var options = new FolderPickerOpenOptions
                {
                    Title = L("PickerOutputTitle"),
                    AllowMultiple = false
                };

                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
                var folder = folders.FirstOrDefault();

                if (folder != null)
                {
                    OutputDirectory = folder.Path.LocalPath;
                    if (_settingsService.Current.OutputDirectory != OutputDirectory)
                    {
                        _settingsService.Current.OutputDirectory = OutputDirectory;
                    }
                    SystemStatus = string.Format(L("OutputDirSet"), OutputDirectory);
                }
            }
        }
        
        [RelayCommand]
        private void ExploreOutputDirectory()
        {
            if (!string.IsNullOrWhiteSpace(OutputDirectory) && Directory.Exists(OutputDirectory))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = OutputDirectory,
                        UseShellExecute = true,
                        Verb = "open"
                    });
                }
                catch (Exception ex)
                {
                    SystemStatus = string.Format(L("ExploreError"), ex.Message);
                }
            }
            else
            {
                SystemStatus = L("OutputDirInvalid");
            }
        }

        [RelayCommand]
        private void TogglePopup()
        {
            IsFlyoutOpen = !IsFlyoutOpen;
        }
           

        [RelayCommand]
        private void TogglePresetsPopup()
        {
            IsPresetsFlyoutOpen = !IsPresetsFlyoutOpen;
        }

        // Selecciona la función de normalización (restablece la cola vía OnAppModeChanged).
        [RelayCommand]
        private void SetNormalizeMode() => AppMode = AppMode.Normalize;

        // Selecciona la función de análisis (restablece la cola vía OnAppModeChanged).
        [RelayCommand]
        private void SetAnalyzeMode() => AppMode = AppMode.Analyze;

        // Conmutador Original/Normalizado de la ficha. El estado es del archivo, así
        // que al cambiar de selección cada uno conserva la vista en la que se dejó.
        // RefreshAnalysisSummary() ya invalida las propiedades de la ficha.
        [RelayCommand]
        private void ShowOriginalMetrics() => SetFileMetricsView(normalized: false);

        [RelayCommand]
        private void ShowNormalizedMetrics() => SetFileMetricsView(normalized: true);

        private void SetFileMetricsView(bool normalized)
        {
            if (SelectedFile == null || SelectedFile.ShowNormalized == normalized) return;
            SelectedFile.ShowNormalized = normalized;
            RefreshAnalysisSummary();
        }

        // Cambia el idioma de la interfaz y persiste la preferencia.
        [RelayCommand]
        private void SetLanguage(string? language)
        {
            LocalizationManager.Instance.SetLanguage(language);
            if (_settingsService.Current.Language != LocalizationManager.Instance.Language)
            {
                _settingsService.Current.Language = LocalizationManager.Instance.Language;
            }
        }

        // Ajuste fino estilo plugin VST. El CommandParameter es "+0.1" / "-0.1" etc.
        // El clamping se aplica dentro de cada OnXxxChanged al reasignar la propiedad.
        [RelayCommand]
        private void AdjustTargetLufs(string? delta)
        {
            if (!TryParseStep(delta, out double d)) return;
            TargetLufs += d;
        }

        [RelayCommand]
        private void AdjustPeakCeiling(string? delta)
        {
            if (!TryParseStep(delta, out double d)) return;
            TruePeakCeiling += d;
        }

        [RelayCommand]
        private void AdjustReleaseTime(string? delta)
        {
            if (!TryParseStep(delta, out double d)) return;
            ReleaseTimeMs += d;
        }

        [RelayCommand]
        private void AdjustLookAheadTime(string? delta)
        {
            if (!TryParseStep(delta, out double d)) return;
            LookAheadTimeMs += d;
        }

        private static bool TryParseStep(string? delta, out double value) =>
            double.TryParse(delta, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        #endregion

        #region Methods
        // Modo "Solo análisis": mide cada archivo original y actualiza las mediciones
        // del grid sin normalizar. Durante el análisis se muestra el spinner (Analyzing)
        // y al terminar el estado refleja el resultado de la tarea (Completed / Error).
        private async Task RunAnalysisOnlyAsync()
        {
            if (IsProcessing) return;

            if (AudioFiles.Count == 0)
            {
                SystemStatus = L("QueueEmptyWarn");
                return;
            }

            ResetModalProgress();

            IsProcessing = true;

            // Solo se analizan los archivos que aún no están completados; los ya
            // medidos conservan su estado Completed y no se vuelven a procesar.
            var filesToMeasure = AudioFiles
                .Where(f => f.Status != AudioFileStatus.Completed)
                .ToList();

            int totalFiles = filesToMeasure.Count;

            if (totalFiles == 0)
            {
                IsProcessing = false;
                ResetModalProgress();
                SystemStatus = L("AllAnalyzed");
                return;
            }

            StartElapsedTimer();

            SystemStatus = string.Format(L("StartAnalysis"), totalFiles);

            var progress = new BatchProgress();
            using var cts = new CancellationTokenSource();
            _batchCts = cts;

            try
            {
                // El análisis puro es ligero en RAM y CPU-bound (SIMD), por lo que admite
                // más concurrencia que la normalización.
                using var semaphore = new SemaphoreSlim(MaxConcurrentAnalyzeTasks);

                var tasks = new Task[totalFiles];
                for (int i = 0; i < tasks.Length; i++)
                {
                    tasks[i] = AnalyzeFileAsync(filesToMeasure[i], semaphore, totalFiles, progress, cts.Token);
                }
                _batchTask = Task.WhenAll(tasks);
                await _batchTask;
            }
            finally
            {
                StopElapsedTimer();
                _batchCts = null;
                _batchTask = null;
                cts.Dispose();
            }

            IsProcessing = false;
            ResetModalProgress();

            SystemStatus = progress.Cancelled > 0
                ? progress.Succeeded > 0
                    ? string.Format(L("AnalysisCancelSummary"), progress.Succeeded, progress.Cancelled, progress.Errors)
                    : L("AnalysisCancelNoResults")
                : progress.Errors == 0
                    ? string.Format(L("AnalysisDone"), totalFiles)
                    : string.Format(L("AnalysisDoneAll"), progress.Succeeded, progress.Errors);

            IsStatusSuccess = progress.Errors == 0 && progress.Cancelled == 0;

            RefreshAnalysisSummary();
        }

        // Analiza UN archivo con límite de concurrencia vía semáforo (ver RunAnalysisOnlyAsync).
        private async Task AnalyzeFileAsync(AudioFileModel file, SemaphoreSlim semaphore, int totalFiles, BatchProgress progress, CancellationToken ct)
        {
            bool acquired = false;
            try
            {
                await semaphore.WaitAsync(ct);
                acquired = true;

                int position = Interlocked.Increment(ref progress.Processed);
                UpdateModalCounters(progress);
                SystemStatus = string.Format(L("AnalyzingFile"), position, totalFiles, file.FileName);

                file.Status = AudioFileStatus.Analyzing;
                file.StatusMessage = "Analizando...";

                var analysis = await Task.Run(() => _audioEngine.AnalyzeAudioFile(file.FilePath, ct), ct);

                file.Peak = analysis.TruePeakDbFormatted;
                file.Loudness = analysis.LoudnessFormatted;
                file.IntegratedLoudness = analysis.IntegratedLoudness;
                file.MaxTruePeakLinear = analysis.MaxTruePeakLinear;
                file.LoudnessRange = analysis.LoudnessRange;
                file.SampleRate = analysis.SampleRate;
                file.Channels = analysis.Channels;
                file.HasMeasurement = true;

                file.Status = AudioFileStatus.Completed;
                file.StatusMessage = "Análisis completado";
                Interlocked.Increment(ref progress.Succeeded);
                UpdateModalCounters(progress);
                SystemStatus = string.Format(L("FileAnalyzed"), position, totalFiles, file.FileName);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref progress.Cancelled);
                UpdateModalCounters(progress);
                file.Status = AudioFileStatus.Pending;
                file.StatusMessage = "Cancelado por el usuario";
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref progress.Errors);
                UpdateModalCounters(progress);
                file.Status = AudioFileStatus.Error;
                file.StatusMessage = $"Error: {ex.Message}";
                SystemStatus = string.Format(L("AnalyzeFileError"), file.FileName, ex.Message);
            }
            finally
            {
                if (acquired) semaphore.Release();
            }
        }

        // Resuelve una lista de paths (archivos y/o carpetas) a archivos de audio válidos.
        // Las carpetas se escanean recursivamente.
        public static List<string> ResolveAudioPaths(IEnumerable<string> paths)
        {
            var result = new List<string>();
            foreach (var p in paths)
            {
                if (Directory.Exists(p))
                {
                    try
                    {
                        result.AddRange(
                            Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories)
                                .Where(f => AudioExtensions.Contains(
                                    Path.GetExtension(f).ToLowerInvariant())));
                    }
                    catch { }
                }
                else if (File.Exists(p) &&
                         AudioExtensions.Contains(
                             Path.GetExtension(p).ToLowerInvariant()))
                {
                    result.Add(p);
                }
            }
            return result;
        }

        /// <summary>
        /// Añade archivos a la cola evitando duplicados; devuelve cuántos se agregaron.
        ///
        /// Leer la duración con ATL implica abrir y parsear la cabecera, así que un
        /// archivo corrupto, truncado o con un RIFF descuadrado hace que ATL lance. Por
        /// eso cada archivo va con su propio try/catch: si no, un solo archivo malo
        /// abortaba la carga entera y los siguientes no entraban nunca. El que falla se
        /// salta y se cuenta en <paramref name="failed"/>, y el resto de la cola sigue.
        ///
        /// Un archivo que no entra no tiene fila donde llevar la X roja, así que el
        /// recuento de fallidos solo se puede comunicar por <c>SystemStatus</c>.
        /// </summary>
        /// <param name="filePaths">Rutas a añadir.</param>
        /// <param name="failed">Cuántos no se pudieron leer. No cuenta duplicados.</param>
        public int AddFilesToQueue(IEnumerable<string> filePaths, out int failed)
        {
            int added = 0;
            failed = 0;

            foreach (var localPath in filePaths)
            {
                if (AudioFiles.Any(f => f.FilePath == localPath)) continue;

                string fileName = Path.GetFileName(localPath);
                string codec = Path.GetExtension(localPath).ToUpper().Replace(".", "");
                string formattedDuration;

                try
                {
                    var track = new ATL.Track(localPath);
                    var duration = TimeSpan.FromSeconds(track.Duration);

                    // ATL puede devolver duración cero, negativa o absurda en cabeceras
                    // truncadas sin lanzar excepción. El archivo entra igual (el análisis
                    // sabrá más), pero la columna no miente mostrando 00:00.
                    formattedDuration = duration <= TimeSpan.Zero || duration.TotalDays > 1
                        ? "—"
                        : duration.ToString(duration.Hours > 0 ? "h\\:mm\\:ss" : "mm\\:ss");
                }
                catch (Exception ex)
                {
                    // No entra en la cola: sin fila no hay X roja que mostrar.
                    failed++;
                    System.Diagnostics.Debug.WriteLine(
                        $"[Queue] No se pudo leer {localPath}: {ex.Message}");
                    continue;
                }

                AudioFiles.Add(new AudioFileModel
                {
                    FilePath = localPath,
                    FileName = fileName,
                    Codec = codec,
                    Duration = formattedDuration,
                    StatusMessage = "Pending",
                    Peak = "—",
                    Loudness = "—",
                    NormalizedPeak = "—",
                    NormalizedLoudness = "—"
                });
                added++;
            }

            if (added > 0) RefreshAnalysisSummary();
            return added;
        }

        /// <summary>
        /// Traduce el resultado de <see cref="AddFilesToQueue"/> a un mensaje de estado,
        /// con el caso de que algunos archivos no se pudieran leer. Los llamadores la usan
        /// para no tener que repetir el árbol de decisiones.
        /// </summary>
        public void ReportQueueResult(int added, int failed)
        {
            if (failed > 0 && added > 0)
            {
                SystemStatus = string.Format(L("QueuePartialFailed"), added, failed);
            }
            else if (failed > 0)
            {
                SystemStatus = string.Format(L("QueueAllFailed"), failed);
            }
            else if (added > 0)
            {
                SystemStatus = string.Format(L("DropAdded"), added);
            }
        }

        private async Task OnAddFile()
        {
            // Obtener de forma segura el StorageProvider desde el ciclo de vida de la App de Avalonia
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            { 
                var topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
                if (topLevel == null) return;

                // Configurar los filtros de búsqueda técnica de audio
                var options = new FilePickerOpenOptions
                {
                    Title = L("PickerAudioTitle"),
                    AllowMultiple = true,
                    FileTypeFilter = new[]
                    {
                new FilePickerFileType(L("PickerAudioType"))
                {
                    Patterns = new[] { "*.wav", "*.flac" }
                }
            }
                };

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(options);

                if (files != null && files.Any())
                {
                    var paths = files
                        .Select(f => f.Path.LocalPath)
                        .ToList();

                    int added = AddFilesToQueue(paths, out int failed);
                    if (failed == 0)
                    {
                        SystemStatus = string.Format(L("FilesAdded"), added);
                    }
                    else
                    {
                        ReportQueueResult(added, failed);
                    }
                }
            }
        }

        // =========================================================================
        // EVENTOS CUANDO EL USUARIO MUEVE LOS CONTROLES NUMÉRICOS (SLIDERS)
        // =========================================================================

        partial void OnTargetLufsChanged(double value)
        {
            // Aplicamos tus límites físicos (Clamping)
            double clamped = Math.Clamp(value, DefaultValues.MIN_TARGET_LUFS, DefaultValues.MAX_TARGET_LUFS);
            clamped = Math.Round(clamped, 1);

            if (Math.Abs(value - clamped) > 0.01)
            {
                TargetLufs = clamped;
                // Aquí sí hacemos return, porque la reasignación volverá a llamar a este método
                return;
            }

            // Solo llegamos aquí cuando el valor ya está clamped
            ReturnToCustomPreset();

            if (_settingsService.Current.TargetLufs != TargetLufs)
            {
                _settingsService.Current.TargetLufs = TargetLufs;
            }

            RefreshAnalysisSummary();
        }

        partial void OnTruePeakCeilingChanged(double value)
        {
            // Aplicamos tus límites físicos personalizados (-3.0 a -0.1)
            double clamped = Math.Clamp(value, DefaultValues.MIN_TRUE_PEAK_CEILING, DefaultValues.MAX_TRUE_PEAK_CEILING);
            clamped = Math.Round(clamped, 1);

            if (Math.Abs(value - clamped) > 0.01)
            {
                TruePeakCeiling = clamped;
                return; // Evitamos bucles infinitos al reasignar
            }

            // Actualizar el modelo de preferencias del usuario
            ReturnToCustomPreset();

            if (_settingsService.Current.TruePeakCeiling != TruePeakCeiling)
            {
                _settingsService.Current.TruePeakCeiling = TruePeakCeiling;
            }

            RefreshAnalysisSummary();
        }

        // Esta función se invoca cuando el usuario mueve el slider de ReleaseTimeMs
        partial void OnLookAheadTimeMsChanged(double value)
        {
            double clamped = Math.Clamp(value, DefaultValues.MIN_LOOK_AHEAD_TIME_MS, DefaultValues.MAX_LOOK_AHEAD_TIME_MS);
            clamped = Math.Round(clamped, 1);

            if (Math.Abs(value - clamped) > 0.01)
            {
                LookAheadTimeMs = clamped;
                return; // Evitamos bucles infinitos al reasignar
            }

            ReturnToCustomPreset();

            if (_settingsService.Current.LookAheadTimeMs != LookAheadTimeMs)
            {
                _settingsService.Current.LookAheadTimeMs = LookAheadTimeMs;
            }
        }

        partial void OnReleaseTimeMsChanged(double value)
        {
            double clamped = Math.Clamp(value, DefaultValues.MIN_RELEASE_TIME_MS, DefaultValues.MAX_RELEASE_TIME_MS);
            clamped = Math.Round(clamped, 1);

            if(Math.Abs(value - clamped) > 0.01)
            {
                ReleaseTimeMs = clamped;
                return; // Evitamos bucles infinitos al reasignar
            }

            ReturnToCustomPreset();

            if (_settingsService.Current.ReleaseTimeMs != ReleaseTimeMs)
            {
                _settingsService.Current.ReleaseTimeMs = ReleaseTimeMs;
            }
        }

        #endregion

        // ---------------------------------------------------------------------------
        // ESTADO DEL MODAL DE CANCELACIÓN
        // ---------------------------------------------------------------------------

        // Devuelve el modal a su estado inicial al iniciar o terminar un lote.
        private void ResetModalProgress()
        {
            CompletedCount = 0;
            InProgressCount = 0;
            ElapsedText = "00:00";
            IsCancelling = false;
        }

        // Recalcula los contadores visibles a partir de los contadores atómicos del lote.
        // "En curso" son los que ya arrancaron y aún no llegaron a un desenlace final.
        // Un archivo fallido cuenta como completado: dejó de estar en curso. El modal
        // no distingue successes de fallos porque, con el lote a medias, ese contador
        // anticipa un resultado que todavía no existe.
        private void UpdateModalCounters(BatchProgress progress)
        {
            int finished = progress.Succeeded + progress.Errors + progress.Cancelled;
            CompletedCount = finished;
            InProgressCount = progress.Processed - finished;
            OnPropertyChanged(nameof(QueueCountersText));
        }

        private void StartElapsedTimer()
        {
            _batchStartTime = DateTime.Now;
            ElapsedText = "00:00";
            _elapsedTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _elapsedTimer.Tick += OnElapsedTick;
            _elapsedTimer.Start();
        }

        private void StopElapsedTimer()
        {
            if (_elapsedTimer != null)
            {
                _elapsedTimer.Stop();
                _elapsedTimer.Tick -= OnElapsedTick;
                _elapsedTimer = null;
            }
        }

        private void OnElapsedTick(object? sender, EventArgs e)
        {
            ElapsedText = FormatElapsed(_batchStartTime);
        }

        private static string FormatElapsed(DateTime start) =>
            (DateTime.Now - start).ToString(@"hh\:mm\:ss");

        // Contadores atómicos del lote paralelo. Se acceden con Interlocked porque
        // varias tareas pueden avanzar a la vez con el semáforo.
        private sealed class BatchProgress
        {
            public int Processed;
            public int Succeeded;
            public int Cancelled;
            public int Errors;
        }

       

    }
}
