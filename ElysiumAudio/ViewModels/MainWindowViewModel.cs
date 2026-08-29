using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElysiumAudio.Models;
using ElysiumAudio.Services;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace ElysiumAudio.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {

        #region Fields
        private const double DEFAULT_TARGET_LUFS = -14.0;
        private const double DEFAULT_TRUE_PEAK_CEILING = -1.0;

        private const double RELEASE_TIME_MS = 50; // Tiempo de liberación del limitador en milisegundos (entre 20 y 100 ms es típico)
        private const double LOOK_AHEAD_TIME_MS = 5; // Tiempo de anticipación del limitador en milisegundos (entre 1 y 10 ms es típico)
        #endregion

        #region Properties
        private readonly AudioEngineService _audioEngine = new();

        [ObservableProperty]
        private string? _targetLufsText = DEFAULT_TARGET_LUFS.ToString("F1", CultureInfo.InvariantCulture);

        [ObservableProperty]
        private string? _truePeakCeilingString = DEFAULT_TRUE_PEAK_CEILING.ToString("F1", CultureInfo.InvariantCulture);

        [ObservableProperty]
        private double _lookAheadMsText = LOOK_AHEAD_TIME_MS;

        [ObservableProperty]
        private double _releaseMsText = RELEASE_TIME_MS;

        // 1. Enlazado al Slider y TextBox numérico de la interfaz
        [ObservableProperty]
        public double _targetLufs = DEFAULT_TARGET_LUFS;

        // 2. Enlazado al TextBox del techo de pico real
        [ObservableProperty]
        public double _truePeakCeiling = DEFAULT_TRUE_PEAK_CEILING;
     

        [ObservableProperty]
        private bool _isStrictLinearMode = true;

        [ObservableProperty]
        private string _systemStatus = "Engine ready. Core optimized for bit-perfect mastering.";

        [ObservableProperty]
        private bool _isProcessing;

        public ObservableCollection<AudioFileModel> AudioFiles { get; } = new();


        #endregion

        #region constructors

        public MainWindowViewModel()
        {
            TruePeakCeilingString = TruePeakCeiling.ToString("F1", CultureInfo.InvariantCulture);
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
            SystemStatus = "Iniciando procesamiento lineal por lotes con clonación de metadatos...";

            foreach (var file in AudioFiles)
            {
                file.Status = "Analyzing...";

                try
                {
                    // PASADA 1: Análisis en segundo plano
                    var analysis = await Task.Run(() => _audioEngine.AnalyzeAudioFile(file.FilePath));

                    file.Peak = analysis.PeakDbFormatted;
                    file.Loudness = analysis.LoudnessFormatted;
                    file.Status = "Processing...";

                    // Parseo directo y ultra seguro de la propiedad validada
                    float maxPeakLimitDb = (float)TruePeakCeiling; // Respaldo por defecto
                    if (float.TryParse(TruePeakCeilingString, System.Globalization.CultureInfo.InvariantCulture, out float parsedPeak))
                    {
                        maxPeakLimitDb = parsedPeak;
                    }

                    // Continuar con el cálculo de ganancia...
                    float requiredGainDb = _audioEngine.CalculateTargetGain(analysis.IntegratedLoudness, (float)TargetLufs);

                    string directory = Path.GetDirectoryName(file.FilePath) ?? "";
                    string filenameWithoutExt = Path.GetFileNameWithoutExtension(file.FilePath);
                    string ext = Path.GetExtension(file.FilePath);
                    string outputPath = Path.Combine(directory, $"{filenameWithoutExt}_normalized{ext}");

                    // PASADA 2: Renderizado físico del audio flotante
                    await Task.Run(() =>
                    {
                        _audioEngine.ApplyNormalizationWithLimiter(
                            file.FilePath,
                            outputPath,
                            requiredGainDb,
                            maxPeakLimitDb,
                            releaseMs: 25f
                        );
                    });

                    // PASO 3 AUTOMATIZADO: Inyección digital de tags e imágenes ID3v2.3 / FLAC
                    file.Status = "Tagging...";
                    await Task.Run(() => _audioEngine.CloneMetadataAndCover(file.FilePath, outputPath));

                    var reanalyze = await Task.Run(() => _audioEngine.AnalyzeAudioFile(outputPath));
                    file.NormalizedLoudness = reanalyze.LoudnessFormatted;
                    file.NormalizedPeak = reanalyze.PeakDbFormatted;

                    file.Status = "Done 🟢";
                    SystemStatus = $"Completado con metadatos: {file.FileName}";
                }
                catch (Exception ex)
                {
                    file.Status = "Error ❌";
                    SystemStatus = $"Error crítico en {file.FileName}: {ex.Message}";
                }
            }

            IsProcessing = false;
            SystemStatus = "¡Normalización por lote completada! Archivos listos con portadas y tags intactos.";
        }

        [RelayCommand]
        private void OnClearList()
        {
            AudioFiles.Clear();
            SystemStatus = "Cola de archivos limpiada.";
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
                            Status = "Pending",
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
            double clamped = Math.Clamp(value, -24.0, -6.0);
            clamped = Math.Round(clamped, 1);

            if (Math.Abs(_targetLufs - clamped) > 0.01)
            {
                _targetLufs = clamped;
            }

            // Sincronizamos la caja de texto de forma reactiva
            string nuevoTexto = _targetLufs.ToString("F1", CultureInfo.InvariantCulture);
            if (TargetLufsText != nuevoTexto)
            {
                TargetLufsText = nuevoTexto;
            }
        }

        partial void OnTruePeakCeilingChanged(double value)
        {
            // Aplicamos tus límites físicos personalizados (-3.0 a -0.1)
            double clamped = Math.Clamp(value, -3.0, -0.1);
            clamped = Math.Round(clamped, 1);

            if (Math.Abs(_truePeakCeiling - clamped) > 0.01)
            {
                _truePeakCeiling = clamped;
            }

            // Sincronizamos la caja de texto de forma reactiva
            string nuevoTexto = _truePeakCeiling.ToString("F1", CultureInfo.InvariantCulture);
            if (TruePeakCeilingString != nuevoTexto)
            {
                TruePeakCeilingString = nuevoTexto;
            }
        }

        // =========================================================================
        // SINK 2: EVENTOS CUANDO EL USUARIO ESCRIBE MANUALMENTE EN LOS TEXTBOX
        // =========================================================================

        partial void OnTargetLufsTextChanged(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "-") return;

            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                // Si el usuario escribe algo fuera de rango, tu lógica lo contiene
                double clamped = Math.Clamp(parsed, -24.0, -6.0);

                if (Math.Abs(TargetLufs - clamped) > 0.01)
                {
                    TargetLufs = clamped; // Esto disparará a su vez OnTargetLufsChanged para formatear el texto
                }
            }
        }

        partial void OnTruePeakCeilingStringChanged(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "-") return;

            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                double clamped = Math.Clamp(parsed, -3.0, -0.1);

                if (Math.Abs(TruePeakCeiling - clamped) > 0.01)
                {
                    TruePeakCeiling = clamped; // Esto disparará OnTruePeakCeilingChanged para formatear el texto
                }
            }
        }
        #endregion


    }
}
