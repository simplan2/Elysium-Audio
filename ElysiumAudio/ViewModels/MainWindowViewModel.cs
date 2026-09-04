using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElysiumAudio.Helpers;
using ElysiumAudio.Models;
using ElysiumAudio.Services;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
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
        private readonly Models.UserSettings _settings;
        #endregion

        #region Properties

        // 1. Enlazado al Slider LUFS y TextBox numérico de la interfaz
        //[ObservableProperty]
        //private string? _targetLufsText = DefaultValues.DEFAULT_TARGET_LUFS.ToString("F1", CultureInfo.InvariantCulture);

        [ObservableProperty]
        public double _targetLufs = DefaultValues.DEFAULT_TARGET_LUFS;
        public double MinTargetLufs { get; set; } = DefaultValues.MIN_TARGET_LUFS;
        public double MaxTargetLufs { get; set; } = DefaultValues.MAX_TARGET_LUFS;

        // 2. Enlazado al TextBox del techo de pico real
        //[ObservableProperty]
        //private string? _truePeakCeilingString = DefaultValues.DEFAULT_TRUE_PEAK_CEILING.ToString("F1", CultureInfo.InvariantCulture);

        [ObservableProperty]
        public double _truePeakCeiling = DefaultValues.DEFAULT_TRUE_PEAK_CEILING;
        public double MinPeakCeiling { get; set; } = DefaultValues.MIN_TRUE_PEAK_CEILING;
        public double MaxPeakCeiling { get; set; } = DefaultValues.MAX_TRUE_PEAK_CEILING;

        // 3. Tiempo de liberacion del limitador en milisegundos
        //[ObservableProperty]
        //private string? _releaseTimeMsText = DefaultValues.DEFAULT_RELEASE_TIME_MS.ToString("F1", CultureInfo.InvariantCulture);

        [ObservableProperty]
        private double _releaseTimeMs = DefaultValues.DEFAULT_RELEASE_TIME_MS;
        public double MinReleaseTimeMs { get; set; } = DefaultValues.MIN_RELEASE_TIME_MS;
        
        public double MaxReleaseTimeMs { get; set; } = DefaultValues.MAX_RELEASE_TIME_MS;     

        // 4. Tiempo de anticipación del limitador en milisegundos
        //[ObservableProperty]
        //private string? _lookAheadTimeMsText = DefaultValues.DEFAULT_LOOK_AHEAD_TIME_MS.ToString("F1", CultureInfo.InvariantCulture);

        [ObservableProperty]
        private double _lookAheadTimeMs = DefaultValues.DEFAULT_LOOK_AHEAD_TIME_MS;

        public double MinLookAheadTimeMs { get; set; } = DefaultValues.MIN_LOOK_AHEAD_TIME_MS;
        public double MaxLookAheadTimeMs { get; set; } = DefaultValues.MAX_LOOK_AHEAD_TIME_MS;



        [ObservableProperty]
        private bool _isStrictLinearMode = true;

        [ObservableProperty]
        private string _systemStatus = "Engine ready. Core optimized for bit-perfect mastering.";

        [ObservableProperty]
        private bool _isProcessing;

        // Directorio de salida donde se guardan los archivos normalizados
        [ObservableProperty]
        private string _outputDirectory = DefaultValues.GetDefaultOutputDirectory()
            ?? Environment.CurrentDirectory;

        public ObservableCollection<AudioFileModel> AudioFiles { get; set; } = new();
       
        public bool ShowDropMessage => AudioFiles.Count == 0;
        #endregion

        #region constructors

        public MainWindowViewModel() : this(new Services.SettingsService())
        {
        }

        public MainWindowViewModel(ISettingsService settingsService)
        {
            _settingsService = settingsService;
            _settings = _settingsService.Load();

            // Cargar las preferencias guardadas del usuario
            TargetLufs = _settings.TargetLufs;
            TruePeakCeiling = _settings.TruePeakCeiling;
            ReleaseTimeMs = _settings.ReleaseTimeMs;
            LookAheadTimeMs = _settings.LookAheadTimeMs;

            if (!string.IsNullOrWhiteSpace(_settings.OutputDirectory) && Directory.Exists(_settings.OutputDirectory))
            {
                OutputDirectory = _settings.OutputDirectory;
            }

            AudioFiles.CollectionChanged += AudioFiles_CollectionChanged;
        }

        private void AudioFiles_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            base.OnPropertyChanged(nameof(ShowDropMessage));
        }

        // Guarda todas las preferencias del modelo en el servicio de persistencia.
        // Se invoca al cerrar la aplicación para escribir los datos una sola vez.
        public void SaveSettings()
        {
            _settingsService.Save(_settings);
        }
        #endregion

        #region Commands
        // Comando para añadir archivos a la cola de procesamiento
        [RelayCommand]
        private async Task AddFile()
        {
            await OnAddFile();
        }

        // Comando para iniciar la normalización por lotes
        [RelayCommand]
        private async Task OnStartNormalizationAsync()
        {
            if (AudioFiles.Count == 0)
            {
                SystemStatus = "Advertencia: La cola de archivos está vacía.";
                return;
            }

            IsProcessing = true;

            // Solo se procesan los archivos que aún no están completados
            var filesToProcess = AudioFiles
                .Where(f => f.Status != AudioFileStatus.Completed)
                .ToList();

            int totalFiles = filesToProcess.Count;
            int processedCount = 0;
            int errorCount = 0;

            if (totalFiles == 0)
            {
                IsProcessing = false;
                SystemStatus = "Todos los archivos ya están completados. No hay nada que procesar.";
                return;
            }

            SystemStatus = $"Iniciando normalización de {totalFiles} archivo(s)...";

            foreach (var file in filesToProcess)
            {
                processedCount++;
                SystemStatus = $"Procesando {processedCount}/{totalFiles}: {file.FileName}";

                try
                {
                    // PASO 1: Análisis
                    file.Status = AudioFileStatus.Analyzing;
                    file.StatusMessage = $"Analizando... (0%)";

                    var analysis = await Task.Run(() => _audioEngine.AnalyzeAudioFile(file.FilePath));

                    file.Peak = analysis.PeakDbFormatted;
                    file.Loudness = analysis.LoudnessFormatted;
                    file.StatusMessage = $"Análisis completado (25%)";

                    // PASO 2: Cálculo de ganancia
                    file.Status = AudioFileStatus.Processing;
                    file.StatusMessage = $"Calculando ganancia... (25%)";

                    float maxPeakLimitDb = (float)TruePeakCeiling;

                    float requiredGainDb = _audioEngine.CalculateTargetGain(analysis.IntegratedLoudness, (float)TargetLufs);

                    string inputDirectory = Path.GetDirectoryName(file.FilePath) ?? "";
                    string filenameWithoutExt = Path.GetFileNameWithoutExtension(file.FilePath);
                    string ext = Path.GetExtension(file.FilePath);

                    // Si el directorio de salida coincide con el de entrada, agregamos "_normalized"
                    // para no pisar el archivo original; si son distintos, conservamos el nombre.
                    string outputDir = OutputDirectory ?? inputDirectory;
                    bool sameDir = string.Equals(
                        Path.GetFullPath(outputDir).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(inputDirectory).TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase);

                    string outputFileName = sameDir
                        ? $"{filenameWithoutExt}_normalized{ext}"
                        : $"{filenameWithoutExt}{ext}";

                    string outputPath = OutputPathHelper.GetUniqueOutputPath(
                        Path.Combine(outputDir, outputFileName));

                    file.StatusMessage = $"Renderizando audio... (50%)";

                    // Valores del limitador desde las preferencias del usuario
                    float releaseMs = (float)ReleaseTimeMs;
                    float lookAheadMs = (float)LookAheadTimeMs;

                    // PASO 3: Renderizado
                    await Task.Run(() =>
                    {
                        _audioEngine.ApplyNormalizationWithLimiter(
                            file.FilePath,
                            outputPath,
                            requiredGainDb,
                            maxPeakLimitDb,
                            releaseMs: releaseMs,
                            lookAheadMs: lookAheadMs
                        );
                    });

                    file.StatusMessage = $"Aplicando metadatos... (75%)";

                    // PASO 4: Metadatos
                    file.Status = AudioFileStatus.Tagging;
                    await Task.Run(() => _audioEngine.CloneMetadataAndCover(file.FilePath, outputPath));

                    file.StatusMessage = $"Verificando resultado... (90%)";

                    var reanalyze = await Task.Run(() => _audioEngine.AnalyzeAudioFile(outputPath));
                    file.NormalizedLoudness = reanalyze.LoudnessFormatted;
                    file.NormalizedPeak = reanalyze.PeakDbFormatted;

                    // Completado
                    file.Status = AudioFileStatus.Completed;
                    file.StatusMessage = $"Completado (100%)";
                    SystemStatus = $"[{processedCount}/{totalFiles}] Completado: {file.FileName}";
                }
                catch (Exception ex)
                {
                    errorCount++;
                    file.Status = AudioFileStatus.Error;
                    file.StatusMessage = $"Error: {ex.Message}";
                    SystemStatus = $"Error en {file.FileName}: {ex.Message}";
                }
            }

            IsProcessing = false;

            if (errorCount == 0)
            {
                SystemStatus = $"Normalización completada: {totalFiles} archivo(s) procesado(s) exitosamente.";
            }
            else
            {
                SystemStatus = $"Proceso finalizado: {totalFiles - errorCount} éxitos, {errorCount} error(es).";
            }
        }

        [RelayCommand]
        private void OnClearList()
        {
            AudioFiles.Clear();
            SystemStatus = "Cola de archivos limpiada.";
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
                    _settings.OutputDirectory = OutputDirectory;
                    SystemStatus = $"Directorio de salida: {OutputDirectory}";
                }
            }
        }

        [RelayCommand]
        private void DefaultNormalizationValues()
        {
            TargetLufs = DefaultValues.DEFAULT_TARGET_LUFS;
            TruePeakCeiling = DefaultValues.DEFAULT_TRUE_PEAK_CEILING;
            ReleaseTimeMs = DefaultValues.DEFAULT_RELEASE_TIME_MS;
            LookAheadTimeMs = DefaultValues.DEFAULT_LOOK_AHEAD_TIME_MS;
        }

        #endregion

        #region Methods

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
                    foreach (var file in files)
                    {
                        string localPath = file.Path.LocalPath;

                        // Evitamos duplicados en la tabla de la interfaz
                        if (AudioFiles.Any(f => f.FilePath == localPath)) continue;

                        // Añadimos a la tabla de forma reactiva
                        AudioFiles.Add(new AudioFileModel
                        {
                            FilePath = localPath,
                            FileName = Path.GetFileName(localPath),
                            Codec = Path.GetExtension(localPath).ToUpper().Replace(".", ""),
                            Duration = "--:--",
                            StatusMessage = "Pending",
                            Peak = "0.0 dBFS",
                            Loudness = "-0.0 LUFS"
                        });
                    }
                    SystemStatus = $"Se añadieron {files.Count} archivos a la cola de procesamiento.";
                }
            }
        }


        // =========================================================================
        // SINK 1: EVENTOS CUANDO EL USUARIO MUEVE LOS CONTROLES NUMÉRICOS (SLIDERS)
        // =========================================================================

        partial void OnTargetLufsChanged(double value)
        {
            // Aplicamos tus límites físicos (Clamping)
            double clamped = Math.Clamp(value, DefaultValues.MIN_TARGET_LUFS, DefaultValues.MAX_TARGET_LUFS);
            clamped = Math.Round(clamped, 1);

            if (Math.Abs(_targetLufs - clamped) > 0.01)
            {
                _targetLufs = clamped;
            }

            // Actualizar el modelo de preferencias del usuario
            _settings.TargetLufs = _targetLufs;
        }

        partial void OnTruePeakCeilingChanged(double value)
        {
            // Aplicamos tus límites físicos personalizados (-3.0 a -0.1)
            double clamped = Math.Clamp(value, DefaultValues.MIN_TRUE_PEAK_CEILING, DefaultValues.MAX_TRUE_PEAK_CEILING);
            clamped = Math.Round(clamped, 1);

            if (Math.Abs(_truePeakCeiling - clamped) > 0.01)
            {
                _truePeakCeiling = clamped;
            }

            // Actualizar el modelo de preferencias del usuario
            _settings.TruePeakCeiling = _truePeakCeiling;
        }

        // Esta función se invoca cuando el usuario mueve el slider de ReleaseTimeMs, y actualiza tanto la propiedad como el texto asociado.
        partial void OnLookAheadTimeMsChanged(double value)
        {
            double clamped = Math.Clamp(value, DefaultValues.MIN_LOOK_AHEAD_TIME_MS, DefaultValues.MAX_LOOK_AHEAD_TIME_MS);
            clamped = Math.Round(clamped, 1);

            if (Math.Abs(_lookAheadTimeMs - clamped) > 0.01)
            {
                _lookAheadTimeMs = clamped;
            }

            _settings.LookAheadTimeMs = _lookAheadTimeMs;
        }

        partial void OnReleaseTimeMsChanged(double value)
        {
            double clamped = Math.Clamp(value, DefaultValues.MIN_RELEASE_TIME_MS, DefaultValues.MAX_RELEASE_TIME_MS);
            clamped = Math.Round(clamped, 1);

            if(Math.Abs(_releaseTimeMs - clamped) > 0.01)
            {
                _releaseTimeMs = clamped;
            }

            _settings.ReleaseTimeMs = _releaseTimeMs;
        }

      
        #endregion


    }
}
