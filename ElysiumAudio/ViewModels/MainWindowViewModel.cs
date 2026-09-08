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
using System.Collections.Generic;
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

        #endregion

        #region Properties

        // 1. Enlazado al Slider LUFS y TextBox numérico de la interfaz
        [ObservableProperty]
        public double _targetLufs = DefaultValues.DEFAULT_TARGET_LUFS;
        public double MinTargetLufs { get; set; } = DefaultValues.MIN_TARGET_LUFS;
        public double MaxTargetLufs { get; set; } = DefaultValues.MAX_TARGET_LUFS;

        // 2. Enlazado al TextBox del techo de pico real
        [ObservableProperty]
        public double _truePeakCeiling = DefaultValues.DEFAULT_TRUE_PEAK_CEILING;
        public double MinPeakCeiling { get; set; } = DefaultValues.MIN_TRUE_PEAK_CEILING;
        public double MaxPeakCeiling { get; set; } = DefaultValues.MAX_TRUE_PEAK_CEILING;

        // 3. Tiempo de liberacion del limitador en milisegundos
        [ObservableProperty]
        private double _releaseTimeMs = DefaultValues.DEFAULT_RELEASE_TIME_MS;
        public double MinReleaseTimeMs { get; set; } = DefaultValues.MIN_RELEASE_TIME_MS;
        public double MaxReleaseTimeMs { get; set; } = DefaultValues.MAX_RELEASE_TIME_MS;

        // 4. Tiempo de anticipación del limitador en milisegundos
        [ObservableProperty]
        private double _lookAheadTimeMs = DefaultValues.DEFAULT_LOOK_AHEAD_TIME_MS;
        public double MinLookAheadTimeMs { get; set; } = DefaultValues.MIN_LOOK_AHEAD_TIME_MS;
        public double MaxLookAheadTimeMs { get; set; } = DefaultValues.MAX_LOOK_AHEAD_TIME_MS;


        [ObservableProperty]
        private string _systemStatus = "Engine ready. Core optimized for bit-perfect mastering.";

        [ObservableProperty]
        private bool _isProcessing;

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
      
        #endregion

        #region Methods

        private static readonly string[] AudioExtensions = { ".wav", ".flac" };

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


    }
}
