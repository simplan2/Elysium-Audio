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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace ElysiumAudio.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        private readonly AudioEngineService _audioEngine = new();

        // 1. Enlazado al Slider y TextBox numérico de la interfaz
        [ObservableProperty]     
        private double _targetLufs = -14.0;

        // 2. Enlazado al TextBox del techo de pico real
        [ObservableProperty]
        private string _truePeakCeilingString = "-1.0";

        [ObservableProperty]
        private bool _isStrictLinearMode = true;

        [ObservableProperty]
        private string _systemStatus = "Engine ready. Core optimized for bit-perfect mastering.";

        [ObservableProperty]
        private bool _isProcessing;

        public ObservableCollection<AudioFileModel> AudioFiles { get; } = new();

        [RelayCommand]
        private async Task AddFile()
        {
            await OnAddFile();
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
                    float maxPeakLimitDb = -1.0f; // Respaldo por defecto
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

        #region Methods

        // VALIDACIÓN 1: Aseguramos que el valor de TargetLufs no exceda -6.0 LUFS
        partial void OnTargetLufsChanged(double value)
        {
            // Si intentan escribir o mover el slider más allá de -6.0 LUFS, se congela en -6.0
            if (value > -6.0)
            {
                TargetLufs = -6.0;
            }
            // Opcional: Límite inferior de seguridad si es necesario
            else if (value < -24.0)
            {
                TargetLufs = -24.0;
            }
        }

        // VALIDACIÓN 2: Acotar el Techo de Pico Real (dBTP)
        partial void OnTruePeakCeilingStringChanged(string value)
        {
            // Evitamos bucles infinitos si la caja se queda vacía momentáneamente mientras el usuario borra
            if (string.IsNullOrWhiteSpace(value) || value == "-" || value == "-0") return;

            if (float.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out float parsedPeak))
            {
                // Regla: Si ponen 0 o valores positivos, se restablece al límite seguro comercial de -0.1
                if (parsedPeak >= 0f)
                {
                    TruePeakCeilingString = "-0.1";
                    SystemStatus = "Ajuste de seguridad: El techo True Peak no puede ser mayor o igual a 0 dBTP.";
                }
                // Regla: Si ponen menos de -12.0 dB, lo acotamos a -12.0 para evitar que la pista quede inaudible
                else if (parsedPeak < -12.0f)
                {
                    TruePeakCeilingString = "-12.0";
                    SystemStatus = "Ajuste de seguridad: El techo mínimo recomendado es -12.0 dBTP.";
                }
            }
        }


        #endregion



        //private async Task OnStartNormalizationAsync()
        //{
        //    if (AudioFiles.Count == 0)
        //    {
        //        SystemStatus = "Advertencia: La cola de archivos está vacía.";
        //        return;
        //    }

        //    IsProcessing = true;
        //    SystemStatus = "Iniciando procesamiento lineal por lotes...";

        //    foreach (var file in AudioFiles)
        //    {
        //        file.Status = "Analyzing...";

        //        try
        //        {
        //            // PASADA 1: Análisis en segundo plano (Usa Spans nativos)
        //            var analysis = await Task.Run(() => _audioEngine.AnalyzeAudioFile(file.FilePath));

        //            // Refrescamos la UI con las mediciones reales del archivo maestro
        //            file.Peak = analysis.PeakDbFormatted;
        //            file.Loudness = analysis.LoudnessFormatted;

        //            file.Status = "Processing...";

        //            // PARSEO BIDIRECCIONAL: Limpiamos la cadena de texto del control True Peak
        //            float maxPeakLimitDb = -1.0f; // Valor de respaldo seguro
        //            string cleanPeak = TruePeakCeiling.Replace(" dBTP", "").Replace("dBTP", "").Trim();
        //            if (float.TryParse(cleanPeak, System.Globalization.CultureInfo.InvariantCulture, out float parsedPeak))
        //            {
        //                maxPeakLimitDb = parsedPeak;
        //            }

        //            // Cálculo de ganancia estática pura (Resta matemática directa sin parches adivinados)
        //            float requiredGainDb = _audioEngine.CalculateTargetGain(analysis.IntegratedLoudness, (float)TargetLufs);

        //            // Aislamos el archivo en la subcarpeta final para tu biblioteca personal
        //            string directory = Path.GetDirectoryName(file.FilePath) ?? "";
        //            string filenameWithoutExt = Path.GetFileNameWithoutExtension(file.FilePath);
        //            string ext = Path.GetExtension(file.FilePath);
        //            string outputPath = Path.Combine(directory, $"{filenameWithoutExt}_normalized{ext}");

        //            // PASADA 2: Renderizado físico del archivo aplicando el Soft-Limiter transparente
        //            await Task.Run(() =>
        //            {
        //                _audioEngine.ApplyNormalizationWithLimiter(
        //                    file.FilePath,
        //                    outputPath,
        //                    requiredGainDb,
        //                    maxPeakLimitDb,
        //                    releaseMs: 25f // Constante de liberación ultra veloz para salvaguardar las baladas
        //                );
        //            });

        //            file.Status = "Done 🟢";
        //            SystemStatus = $"Procesado con éxito: {file.FileName}";
        //        }
        //        catch (Exception ex)
        //        {
        //            file.Status = "Error ❌";
        //            SystemStatus = $"Error crítico en {file.FileName}: {ex.Message}";
        //        }
        //    }

        //    IsProcessing = false;
        //    SystemStatus = "¡Normalización por lote completada! Dinámica y microdinámica protegidas.";
        //}
    }
}
