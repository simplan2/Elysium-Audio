using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ElysiumAudio.ViewModels;
using NAudio.SoundFile;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ElysiumAudio.Views
{
    public partial class MainWindow : Window
    {

        // Propiedad segura que extrae el ViewModel real que App.axaml.cs le inyectó a la ventana
        private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;
        private static readonly Regex NumericRegex = new Regex(@"^-?\d+(\.\d+)?$", RegexOptions.Compiled);

        public MainWindow()
        {
            InitializeComponent();

            // Buscamos el contenedor central por su nombre y le asignamos los eventos de arrastre
            var dropZone = this.FindControl<Border>("DropZoneBorder");
            if (dropZone != null)
            {
                dropZone.AddHandler(DragDrop.DragOverEvent, OnDragOver);
                dropZone.AddHandler(DragDrop.DropEvent, OnDrop);
            }
        }

        // Este método se llama cuando un archivo es arrastrado sobre la ventana. Si el archivo es válido, se permite el arrastre.
        private void OnDragOver(object? sender, DragEventArgs e)
        {
            if (e.DataTransfer.Formats.Contains(DataFormat.File))
            {
                e.DragEffects = DragDropEffects.Copy;
            }
            else
            {
                e.DragEffects = DragDropEffects.None;
            }
        }

        // Este método se llama cuando un archivo es soltado sobre la ventana. Se filtran los archivos válidos y se agregan a la lista de reproducción.
        private async void OnDrop(object? sender, DragEventArgs e)
        {
            if (e.DataTransfer.Formats.Contains(DataFormat.File))
            {
                var files = e.DataTransfer.TryGetFiles();
                var vm = ViewModel;
                if (files != null && vm != null)
                {
                    var validPaths = new List<string>();
                    foreach (var file in files)
                    {
                        string? localPath = file.TryGetLocalPath();
                        if (string.IsNullOrEmpty(localPath)) continue;

                        string path = Path.GetFullPath(localPath)
                            .Normalize(NormalizationForm.FormC);

                        string ext = Path.GetExtension(path).ToLowerInvariant();
                        if (ext == ".wav" || ext == ".flac")
                        {
                            validPaths.Add(path);
                        }
                    }

                    if (validPaths.Count == 0) return;

                    vm.SystemStatus = $"Cargando {validPaths.Count} archivo(s)...";

                    var existingPaths = vm.AudioFiles
                        .Select(f => f.FilePath.Normalize(NormalizationForm.FormC))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    await Task.Run(() =>
                    {
                        foreach (var path in validPaths)
                        {
                            if (existingPaths.Contains(path)) continue;

                            try
                            {
                                var fileInfo = new FileInfo(path);

                                // Usar ATL para leer duración y metadatos
                                var track = new ATL.Track(path);
                                TimeSpan duration = TimeSpan.FromSeconds(track.Duration);
                                string durationFormatted = duration.ToString(
                                    duration.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");

                                var audioModel = new Models.AudioFileModel
                                {
                                    FilePath = path,
                                    FileName = fileInfo.Name,
                                    Codec = fileInfo.Extension.ToUpper().Replace(".", ""),
                                    Duration = durationFormatted,
                                    StatusMessage = "Pending",
                                    Peak = "—",
                                    Loudness = "—",
                                    NormalizedPeak = "—",
                                    NormalizedLoudness = "—"
                                };

                                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                {
                                    vm.AudioFiles.Add(audioModel);
                                });
                            }
                            catch (Exception ex)
                            {
                                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                {
                                    vm.SystemStatus = $"Error de lectura en {Path.GetFileName(path)}: {ex.Message}";
                                });
                            }
                        }
                    });

                    vm.SystemStatus = $"¡Cola de reproducción actualizada con éxito!";
                }
            }
        }

        private void OnTargetLufsKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is TextBox tb)
            {
                if (tb != null && !string.IsNullOrEmpty(tb.Text))
                {
                    ValidateTargetLufs(tb.Text, tb);
                }
            }
        }

        private void OnTargetLufsLostFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                if (tb != null && !string.IsNullOrEmpty(tb.Text))
                {
                    ValidateTargetLufs(tb.Text, tb);
                }
            }
        }

        private void ValidateTargetLufs(string input, TextBox tb)
        {
            if (ViewModel != null)
                // Validar que el input sea un número válido y esté en el rango permitido
                if (NumericRegex.IsMatch(input) &&
                    double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    ViewModel.TargetLufs = parsed; // clamp en el setter
                }
                else
                {
                    // Si no cumple el formato, restaurar el valor actual
                    // Esto evita que se quede texto inválido
                    tb.Text = ViewModel.TargetLufs.ToString("F1", CultureInfo.InvariantCulture);

                }
        }

        // Validación de TruePeakCeiling
        private void OnTruePeakKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is TextBox tb)
            {
                if (!string.IsNullOrEmpty(tb.Text))
                    ValidateTruePeak(tb.Text, tb);
            }
        }

        private void OnTruePeakLostFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                if (!string.IsNullOrEmpty(tb.Text))
                    ValidateTruePeak(tb.Text, tb);
            }
        }

        private void ValidateTruePeak(string input, TextBox tb)
        {
            if (ViewModel == null) return;
            if (NumericRegex.IsMatch(input) &&
                double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                ViewModel.TruePeakCeiling = parsed; // clamp en el setter
                tb.Text = ViewModel.TruePeakCeiling.ToString("F1", CultureInfo.InvariantCulture);
            }
            else
            {
                // Restaurar el valor en caso de entrada de caracteres inválidos
                tb.Text = ViewModel.TruePeakCeiling.ToString("F1", CultureInfo.InvariantCulture);
            }
        }

        // Validación de ReleaseTimeMs
        private void OnReleaseTimeMsKeyDown(object? sender, KeyEventArgs e)
        {
            if ((e.Key == Key.Enter && sender is TextBox tb))
            {
                if (!string.IsNullOrEmpty(tb.Text))
                {
                    ValidateReleaseTimeMs(tb.Text, tb);
                }
            }
        }
        
        private void OnReleaseTimeMsLostFocus(object? sender, RoutedEventArgs e)
        {
            if(sender is TextBox tb)
            {
                if (!string.IsNullOrWhiteSpace(tb.Text))
                {
                    ValidateReleaseTimeMs(tb.Text, tb);
                }
            }
        }

        private void ValidateReleaseTimeMs(string input, TextBox tb)
        {
            if (ViewModel == null) return;
            if (NumericRegex.IsMatch(input) &&
                double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                ViewModel.ReleaseTimeMsText = parsed.ToString("F1", CultureInfo.InvariantCulture); // clamp en el setter
                tb.Text = ViewModel.ReleaseTimeMsText;
            }
            else
            {
                // Restaurar el valor en caso de entrada de caracteres inválidos
                tb.Text = ViewModel.ReleaseTimeMsText;
            }
        }


        // Validación de LookAheadTimeMs
        private void OnLookAheadKeyDown(object? sender, KeyEventArgs e)
        {
            if(e.Key == Key.Enter && sender is TextBox tb)
            {
                if (!string.IsNullOrEmpty(tb.Text))
                {
                    ValidateLookAheadTimeMs(tb.Text, tb);
                }
            }
        }

        private void OnLookAheadLostFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                if (!string.IsNullOrWhiteSpace(tb.Text))
                {
                    ValidateLookAheadTimeMs(tb.Text, tb);
                }
            }
        }
        private void ValidateLookAheadTimeMs(string text, TextBox tb)
        {
            if(ViewModel == null) return;
            if(NumericRegex.IsMatch(text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                ViewModel.LookAheadTimeMsText = parsed.ToString("F1", CultureInfo.InvariantCulture); // clamp en el setter
                tb.Text = ViewModel.LookAheadTimeMsText;
            }
            else
            {
                // Restaurar el valor en caso de entrada de caracteres inválidos
                tb.Text = ViewModel.LookAheadTimeMsText;
            }
        }

       
    }
}
