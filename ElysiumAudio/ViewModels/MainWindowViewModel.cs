using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElysiumAudio.Helpers;
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

        private string _systemStatus = "Engine ready. Core optimized for bit-perfect mastering.";

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

        // --- Progreso del lote mostrado en el modal de cancelación ---
        // 0..100 para la barra de progreso; texto "3 / 10"; archivo y detalle de la
        // etapa actual (se apunta al modelo en curso para que su StatusMessage se
        // refleje en vivo). IsCancelling desactiva el botón tras cancelar.
        [ObservableProperty]
        private double _queueProgress;

        [ObservableProperty]
        private string _queueProgressText = "";

        [ObservableProperty]
        private AudioFileModel? _currentProcessing;

        [ObservableProperty]
        private bool _isCancelling;

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

        // Título y descripción de la función activa, mostrados en la cabecera del panel derecho.
        public string FunctionTitle => IsAnalyzeMode ? "Analyze Loudness" : "Normalize Audio";

        public string FunctionDescription => IsAnalyzeMode
            ? "Measures loudness and true peak only. Original files are never modified."
            : "Applies loudness normalization and true-peak limiting to the output files.";

        // Formato de salida para los archivos normalizados
        [ObservableProperty]
        private OutputFormat _outputFormat = OutputFormat.SameAsSource;

        // Opciones legibles para el ComboBox (los nombres deben coincidir con OutputFormat)
        public string[] OutputFormatOptions { get; } = { "Mantener formato original", "WAV", "FLAC" };

        private static string FormatToOptionText(OutputFormat format) => format switch
        {
            OutputFormat.Wav => "WAV",
            OutputFormat.Flac => "FLAC",
            _ => "Mantener formato original"
        };

        private string _selectedFormat = "WAV";
        public string SelectedFormat
        {
            get => _selectedFormat;
            set
            {
                if (_selectedFormat != value)
                {
                    _selectedFormat = value;
                    OnPropertyChanged(nameof(SelectedFormat));
                    // Actualizar la propiedad OutputFormat según la selección del ComboBox
                    OutputFormat = value switch
                    {
                        "Mantener formato original" => OutputFormat.SameAsSource,
                        "WAV" => OutputFormat.Wav,
                        "FLAC" => OutputFormat.Flac,
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
            : SelectedFile.Channels switch { 1 => "Mono", 2 => "Stereo", _ => $"{SelectedFile.Channels} ch" };

        public string SelectedFileLoudnessText =>
            SelectedFile == null || !SelectedFile.HasMeasurement ? "—" : SelectedFile.Loudness;

        public string SelectedFilePeakText =>
            SelectedFile == null || !SelectedFile.HasMeasurement ? "—" : SelectedFile.Peak;

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

        // Valor y unidad del loudness del archivo seleccionado (presentación en grande).
        public string SelectedFileLoudnessValue =>
            SelectedFile?.HasMeasurement == true
                ? $"{SelectedFile.IntegratedLoudness:F1}"
                : "—";

        public string SelectedFileLoudnessUnit =>
            SelectedFile?.HasMeasurement == true ? "LUFS" : "";

        // Estado de ganancia del archivo seleccionado respecto al target.
        public string SelectedFileGainStatus
        {
            get
            {
                if (SelectedFile == null || !SelectedFile.HasMeasurement) return "—";
                float gain = (float)TargetLufs - SelectedFile.IntegratedLoudness;
                if (Math.Abs(gain) <= 0.25) return "On target";
                return gain > 0 ? "Needs gain" : "Too loud";
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
            OnPropertyChanged(nameof(SummaryTargetText));
            OnPropertyChanged(nameof(SummaryOnTargetText));
            OnPropertyChanged(nameof(SummaryAvgGainText));
            OnPropertyChanged(nameof(SelectedFileGainNeeded));
            OnPropertyChanged(nameof(SelectedFileSampleRateText));
            OnPropertyChanged(nameof(SelectedFileChannelsText));
            OnPropertyChanged(nameof(SelectedFileLoudnessText));
            OnPropertyChanged(nameof(SelectedFilePeakText));
            OnPropertyChanged(nameof(SelectedFileLoudnessValue));
            OnPropertyChanged(nameof(SelectedFileLoudnessUnit));
            OnPropertyChanged(nameof(SelectedFileGainStatus));
            OnPropertyChanged(nameof(SelectedFileGainBrush));
            OnPropertyChanged(nameof(SelectedFileGainBgBrush));
        }

        partial void OnSelectedFileChanged(AudioFileModel? value)
        {
            OnPropertyChanged(nameof(HasSelectedFile));
            OnPropertyChanged(nameof(SelectedFileGainNeeded));
            OnPropertyChanged(nameof(SelectedFileSampleRateText));
            OnPropertyChanged(nameof(SelectedFileChannelsText));
            OnPropertyChanged(nameof(SelectedFileLoudnessText));
            OnPropertyChanged(nameof(SelectedFilePeakText));
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

            // Cargar los presets predefinidos para plataformas de streaming
            LoadPresets();

            // Reaccionar a cambios externos del modelo de configuración
            _settingsService.SettingsChanged += OnSettingsChanged;
        }

        private void LoadPresets()
        {
            // "Predeterminado" siempre usa los valores de fábrica (DefaultValues).
            // Al aplicarlo, los handlers lo devuelven a Custom al primer ajuste manual.
            Presets.Add(new NormalizationPreset
            {
                Name = "Predeterminado",
                TargetLufs = DefaultValues.DEFAULT_TARGET_LUFS,
                TruePeakCeiling = DefaultValues.DEFAULT_TRUE_PEAK_CEILING,
                ReleaseTimeMs = DefaultValues.DEFAULT_RELEASE_TIME_MS,
                LookAheadTimeMs = DefaultValues.DEFAULT_LOOK_AHEAD_TIME_MS
            });

            Presets.Add(new NormalizationPreset
            {
                Name = "Custom",
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
                Name = "Podcast / Narración",
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
            OnPropertyChanged(nameof(FunctionTitle));
            OnPropertyChanged(nameof(FunctionDescription));

            RefreshAnalysisSummary();

            SystemStatus = IsAnalyzeMode
                ? "Modo: Análisis de loudness."
                : "Modo: Normalización de audio.";
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
                Title = "Selecciona una carpeta con pistas de audio",
                AllowMultiple = false
            };

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
            var folder = folders.FirstOrDefault();
            if (folder == null) return;

            string dirPath = folder.Path.LocalPath;

            var files = ResolveAudioPaths(new[] { dirPath });

            if (files.Count == 0)
            {
                SystemStatus = "La carpeta seleccionada no contiene archivos WAV o FLAC.";
                return;
            }

            int added = AddFilesToQueue(files);
            SystemStatus = $"Se añadieron {added} archivo(s) a la cola desde: {Path.GetFileName(dirPath)}";
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
                SystemStatus = "Advertencia: La cola de archivos está vacía.";
                return;
            }

            IsCancelling = false;
            CurrentProcessing = null;
            QueueProgress = 0;

            IsProcessing = true;

            // Solo se procesan los archivos que aún no están completados
            var filesToProcess = AudioFiles
                .Where(f => f.Status != AudioFileStatus.Completed)
                .ToList();

            int totalFiles = filesToProcess.Count;

            if (totalFiles == 0)
            {
                IsProcessing = false;
                IsCancelling = false;
                CurrentProcessing = null;
                SystemStatus = "Todos los archivos ya están completados. No hay nada que procesar.";
                return;
            }

            SystemStatus = $"Iniciando normalización de {totalFiles} archivo(s)...";

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
                _batchCts = null;
                _batchTask = null;
                cts.Dispose();
            }

            IsProcessing = false;
            IsCancelling = false;
            CurrentProcessing = null;

            if (progress.Cancelled > 0)
            {
                SystemStatus = progress.Succeeded > 0
                    ? $"Procesamiento cancelado: {progress.Succeeded} completados, {progress.Cancelled} cancelados, {progress.Errors} error(es)."
                    : "Procesamiento cancelado por el usuario.";
                IsStatusSuccess = false;
            }
            else if (progress.Errors == 0)
            {
                SystemStatus = $"Normalización completada: {totalFiles} archivo(s) procesado(s) exitosamente.";
                IsStatusSuccess = true;
            }
            else
            {
                SystemStatus = $"Proceso finalizado: {progress.Succeeded} éxitos, {progress.Errors} error(es).";
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
                QueueProgress = totalFiles > 0 ? (double)position / totalFiles * 100.0 : 0.0;
                QueueProgressText = $"{position} / {totalFiles}";
                CurrentProcessing = file;
                SystemStatus = $"Procesando {position}/{totalFiles}: {file.FileName}";

                // PASO 1: Análisis
                file.Status = AudioFileStatus.Analyzing;
                file.StatusMessage = $"Analizando... (0%)";

                var analysis = await Task.Run(() => _audioEngine.AnalyzeAudioFile(file.FilePath, ct), ct);

                file.Peak = analysis.TruePeakDbFormatted;
                file.Loudness = analysis.LoudnessFormatted;
                file.IntegratedLoudness = analysis.IntegratedLoudness;
                file.MaxTruePeakLinear = analysis.MaxTruePeakLinear;
                file.SampleRate = analysis.SampleRate;
                file.Channels = analysis.Channels;
                file.HasMeasurement = true;
                file.StatusMessage = "Análisis completado (25%)";

                // PASO 2: Cálculo de ganancia
                file.Status = AudioFileStatus.Processing;
                file.StatusMessage = "Calculando ganancia... (25%)";

                float maxPeakLimitDb = (float)TruePeakCeiling;
                float requiredGainDb = _audioEngine.CalculateTargetGain(analysis.IntegratedLoudness, (float)TargetLufs);

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
                        requiredGainDb,
                        maxPeakLimitDb,
                        releaseMs: releaseMs,
                        lookAheadMs: lookAheadMs,
                        targetLufs: (float)TargetLufs,
                        ct: ct
                    );
                }, ct);

                file.NormalizedLoudness = normalizeResult.LoudnessFormatted;
                file.NormalizedPeak = normalizeResult.TruePeakDbFormatted;
                file.StatusMessage = "Renderizado completado (90%)";

                // PASO 4: Metadatos (después del renderizado, que re-escribe outputPath
                // y sobrescribiría el archivo etiquetado).
                file.Status = AudioFileStatus.Tagging;
                file.StatusMessage = "Aplicando metadatos... (98%)";
                await Task.Run(() => MetadataService.CloneMetadataAndCover(file.FilePath, outputPath));

                // Completado
                file.Status = AudioFileStatus.Completed;
                file.StatusMessage = "Completado (100%)";
                Interlocked.Increment(ref progress.Succeeded);
                SystemStatus = $"[{position}/{totalFiles}] Completado: {file.FileName}";
            }
            catch (OperationCanceledException)
            {
                // El usuario cerró la app o canceló el lote: se deja el archivo en Pending
                // para que pueda reprocesarse, sin marcar error (no es un fallo).
                Interlocked.Increment(ref progress.Cancelled);
                file.Status = AudioFileStatus.Pending;
                file.StatusMessage = "Cancelado por el usuario";
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref progress.Errors);
                file.Status = AudioFileStatus.Error;
                file.StatusMessage = $"Error: {ex.Message}";
                SystemStatus = $"Error en {file.FileName}: {ex.Message}";
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
            SystemStatus = "Cola de archivos limpiada.";
            RefreshAnalysisSummary();
        }

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
            }

            RefreshAnalysisSummary();
            SystemStatus = "Estado de la cola restablecido. Todos los archivos esperan reprocesarse.";
        }

        // Cancela cooperativamente el lote en curso desde el modal de progreso.
        // Las tareas activas sueltan sus temporales y no copian nada parcial al destino.
        [RelayCommand]
        private void CancelProcessing()
        {
            if (IsCancelling) return;
            IsCancelling = true;
            IsStatusSuccess = false;
            SystemStatus = "Cancelando el procesamiento... Los archivos en curso se descartarán de forma segura.";
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
                    Title = "Selecciona el directorio de salida",
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
                    SystemStatus = $"Directorio de salida: {OutputDirectory}";
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
                    SystemStatus = $"Error al abrir el directorio: {ex.Message}";
                }
            }
            else
            {
                SystemStatus = "El directorio de salida no es válido o no existe.";
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
                SystemStatus = "Advertencia: La cola de archivos está vacía.";
                return;
            }

            IsCancelling = false;
            CurrentProcessing = null;
            QueueProgress = 0;

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
                IsCancelling = false;
                CurrentProcessing = null;
                SystemStatus = "Todos los archivos ya están analizados. No hay nada que procesar.";
                return;
            }

            SystemStatus = $"Iniciando análisis de {totalFiles} archivo(s)...";

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
                _batchCts = null;
                _batchTask = null;
                cts.Dispose();
            }

            IsProcessing = false;
            IsCancelling = false;
            CurrentProcessing = null;

            SystemStatus = progress.Cancelled > 0
                ? progress.Succeeded > 0
                    ? $"Análisis cancelado: {progress.Succeeded} medidos, {progress.Cancelled} cancelados, {progress.Errors} error(es)."
                    : "Análisis cancelado por el usuario."
                : progress.Errors == 0
                    ? $"Análisis completado: {totalFiles} archivo(s) medidos."
                    : $"Análisis finalizado: {progress.Succeeded} éxitos, {progress.Errors} error(es).";

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
                QueueProgress = totalFiles > 0 ? (double)position / totalFiles * 100.0 : 0.0;
                QueueProgressText = $"{position} / {totalFiles}";
                CurrentProcessing = file;
                SystemStatus = $"Analizando {position}/{totalFiles}: {file.FileName}";

                file.Status = AudioFileStatus.Analyzing;
                file.StatusMessage = "Analizando...";

                var analysis = await Task.Run(() => _audioEngine.AnalyzeAudioFile(file.FilePath, ct), ct);

                file.Peak = analysis.TruePeakDbFormatted;
                file.Loudness = analysis.LoudnessFormatted;
                file.IntegratedLoudness = analysis.IntegratedLoudness;
                file.MaxTruePeakLinear = analysis.MaxTruePeakLinear;
                file.SampleRate = analysis.SampleRate;
                file.Channels = analysis.Channels;
                file.HasMeasurement = true;

                file.Status = AudioFileStatus.Completed;
                file.StatusMessage = "Análisis completado";
                Interlocked.Increment(ref progress.Succeeded);
                SystemStatus = $"[{position}/{totalFiles}] Analizado: {file.FileName}";
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref progress.Cancelled);
                file.Status = AudioFileStatus.Pending;
                file.StatusMessage = "Cancelado por el usuario";
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref progress.Errors);
                file.Status = AudioFileStatus.Error;
                file.StatusMessage = $"Error: {ex.Message}";
                SystemStatus = $"Error analizando {file.FileName}: {ex.Message}";
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

        // Añade archivos a la cola evitando duplicados; devuelve cuántos se agregaron.
        public int AddFilesToQueue(IEnumerable<string> filePaths)
        {
            int added = 0;
            foreach (var localPath in filePaths)
            {
                if (AudioFiles.Any(f => f.FilePath == localPath)) continue;

                var track = new ATL.Track(localPath);
                TimeSpan duration = TimeSpan.FromSeconds(track.Duration);
                string formattedDuration = duration.ToString(
                    duration.Hours > 0 ? "h\\:mm\\:ss" : "mm\\:ss");

                AudioFiles.Add(new AudioFileModel
                {
                    FilePath = localPath,
                    FileName = Path.GetFileName(localPath),
                    Codec = Path.GetExtension(localPath).ToUpper().Replace(".", ""),
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
                    Title = "Selecciona tus pistas de audio FLAC / WAV",
                    AllowMultiple = true,
                    FileTypeFilter = new[]
                    {
                new FilePickerFileType("Audio de Alta Fidelidad")
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

                    int added = AddFilesToQueue(paths);
                    SystemStatus = $"Se añadieron {added} archivos a la cola de procesamiento.";
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
