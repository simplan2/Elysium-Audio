using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ElysiumAudio.ViewModels;
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

        // Control del cierre durante un procesamiento: el primer intento se cancela,
        // se confirma con el usuario y, si sale, se cancela el lote de forma limpia
        // antes de permitir el cierre real de la ventana.
        private bool _allowClose;
        private bool _handlingClose;

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

        // Este método se llama cuando archivos o carpetas son soltados sobre la ventana.
        // Resuelve archivos WAV/FLAC recursivamente y los agrega a la cola.
        private async void OnDrop(object? sender, DragEventArgs e)
        {
            if (!e.DataTransfer.Formats.Contains(DataFormat.File)) return;

            var files = e.DataTransfer.TryGetFiles();
            var vm = ViewModel;
            if (files == null || vm == null) return;

            var rawPaths = files
                .Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => Path.GetFullPath(p!).Normalize(NormalizationForm.FormC))
                .ToList();

            var validPaths = MainWindowViewModel.ResolveAudioPaths(rawPaths);

            if (validPaths.Count == 0)
            {
                vm.SystemStatus = "No se encontraron archivos WAV / FLAC.";
                return;
            }

            vm.SystemStatus = $"Cargando {validPaths.Count} archivo(s)...";

            int added = vm.AddFilesToQueue(validPaths);
            vm.SystemStatus = added > 0
                ? $"Se añadieron {added} archivo(s) a la cola."
                : "Todos los archivos ya están en la cola.";
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
                    tb.Text = ViewModel.TargetLufs.ToString("F1", CultureInfo.InvariantCulture);
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
            if (sender is TextBox tb)
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
                ViewModel.ReleaseTimeMs = parsed; // clamp en el setter
                tb.Text = ViewModel.ReleaseTimeMs.ToString("F1", CultureInfo.InvariantCulture);
            }
            else
            {
                // Restaurar el valor en caso de entrada de caracteres inválidos
                tb.Text = ViewModel.ReleaseTimeMs.ToString("F1", CultureInfo.InvariantCulture);
            }
        }


        // Validación de LookAheadTimeMs
        private void OnLookAheadKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is TextBox tb)
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
        private void ValidateLookAheadTimeMs(string input, TextBox tb)
        {
            if (ViewModel == null) return;
            // Validar que el input sea un número válido y esté en el rango permitido
            if (NumericRegex.IsMatch(input) &&
                double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                ViewModel.LookAheadTimeMs = parsed; // clamp en el setter
                tb.Text = ViewModel.LookAheadTimeMs.ToString("F1", CultureInfo.InvariantCulture);
            }
            else
            {
                // Restaurar el valor en caso de entrada de caracteres inválidos
                tb.Text = ViewModel.LookAheadTimeMs.ToString("F1", CultureInfo.InvariantCulture);
            }
        }

        // cierra el flyout de Ouput format
        private void OnFlyoutClosed(object sender, EventArgs e)
        {
            // Sincronizar con ViewModel si es necesario
            if (ViewModel != null)
            {
                ViewModel.IsFlyoutOpen = false;
            }
        }

        // Cierra el flyout de Presets
        private void OnPresetsFlyoutClosed(object sender, EventArgs e)
        {
            // Sincronizar con ViewModel si es necesario
            if (ViewModel != null)
            {
                ViewModel.IsPresetsFlyoutOpen = false;
            }
        }

        // Intercepta el cierre de la ventana (click en la X, Alt+F4, etc.).
        // Si hay un lote en curso, el cierre se aplaza hasta que el usuario decida:
        //   - "Seguir procesando": la ventana permanece abierta.
        //   - "Cancelar y salir": se cancela el lote cooperativamente (los archivos a
        //     medio renderizar se descartan sin daños) y, al terminar, se cierra.
        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (_allowClose)
            {
                base.OnClosing(e);
                return;
            }

            e.Cancel = true;
            if (_handlingClose) return;
            _handlingClose = true;
            _ = HandleCloseAsync();
        }

        private async Task HandleCloseAsync()
        {
            try
            {
                var vm = ViewModel;
                if (vm is { IsProcessing: true })
                {
                    var dialog = new ConfirmExitWindow();
                    bool? exit;
                    try
                    {
                        exit = await dialog.ShowDialog<bool>(this);
                    }
                    catch
                    {
                        exit = null; // sin decisión clara → seguimos procesando
                    }

                    if (exit != true)
                    {
                        return; // el usuario prefiere seguir trabajando
                    }

                    // Cancelación cooperativa: las tareas activas sueltan sus temporales
                    // y no copian nada parcial al destino.
                    vm.RequestCancellation();
                    var batch = vm.CurrentBatchTask;
                    if (batch != null)
                    {
                        try
                        {
                            await batch;
                        }
                        catch
                        {
                            // El lote ya terminó o falló mientras tanto; nada que esperar.
                        }
                    }
                }

                _allowClose = true;
                Close();
            }
            finally
            {
                _handlingClose = false;
            }
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            // Forzar re-evaluación del Value después de que Min/Max estén listos
            var slider = this.FindControl<Slider>("TruePeakCeilingSlider");
            if (slider != null && DataContext is MainWindowViewModel vm)
            {
                slider.Minimum = vm.MinTruePeakCeiling;
                slider.Maximum = vm.MaxTruePeakCeiling;
                slider.Value = vm.TruePeakCeiling;
            }
        }
    }
}
