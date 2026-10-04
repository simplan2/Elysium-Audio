using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ElysiumAudio.Localization;
using ElysiumAudio.Models;
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
            var dropZoneRight = this.FindControl<Border>("DropZoneRight");
            if (dropZone != null)
            {
                dropZone.AddHandler(DragDrop.DragOverEvent, OnDragOver);
                dropZone.AddHandler(DragDrop.DropEvent, OnDrop);
            }
            if(dropZoneRight != null)
            {
                dropZoneRight.AddHandler(DragDrop.DragOverEvent, OnDragOver);
                dropZoneRight.AddHandler(DragDrop.DropEvent, OnDrop);
            }
        }

        // Supr quita de la lista lo que esté seleccionado en la tabla. Va por KeyDown
        // y no como Binding porque DataGrid expone SelectedItems como colección de
        // solo lectura: no hay forma de enlazarla a un ICommand desde XAML.
        //
        // Se ignora cuando el foco está en un campo de texto, donde Supr debe seguir
        // siendo "borrar el caracter a la derecha" y no tocar la cola.
        private void OnQueueKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete) return;
            if (IsEditingText(e.Source as Avalonia.Input.IInputElement)) return;

            if (this.FindControl<DataGrid>("AudioFilesGrid") is not { } grid) return;

            var selected = grid.SelectedItems?
                .OfType<AudioFileModel>()
                .ToList();

            if (selected is null || selected.Count == 0) return;

            ViewModel?.RemoveSelectedFiles(selected);

            // Si no se marca como manejada, Supr puede seguir burbujeando hacia arriba
            // y acabar borrando una fila distinta de la que el usuarioerge.
            e.Handled = true;
        }

        private static bool IsEditingText(Avalonia.Input.IInputElement? element) =>
            element is TextBox or AutoCompleteBox;

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
        private static string L(string key) => LocalizationManager.Instance.Get(key);

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
                vm.SystemStatus = L("DropNoFiles");
                return;
            }

            vm.SystemStatus = string.Format(L("DropLoading"), validPaths.Count);

            int added = vm.AddFilesToQueue(validPaths, out int failed);
            if (failed == 0)
            {
                vm.SystemStatus = added > 0
                    ? string.Format(L("DropAdded"), added)
                    : L("DropAllInQueue");
            }
            else
            {
                // Algunos no se pudieron leer. No hay fila donde poner una X roja,
                // porque un archivo que no entra en la cola no existe en la tabla,
                // así que el aviso solo puede ir por la barra de estado.
                vm.ReportQueueResult(added, failed);
            }
        }

        // Los MenuItem del flyout del SplitButton no heredan el DataContext
        // (viven en un popup), así que se conectan aquí a los comandos del VM.
        private void OnAddFilesMenuClick(object? sender, RoutedEventArgs e)
        {
            if (ViewModel?.AddFileCommand is { } cmd && cmd.CanExecute(null))
            {
                cmd.Execute(null);
            }
        }

        private void OnAddFolderMenuClick(object? sender, RoutedEventArgs e)
        {
            if (ViewModel?.AddDirectoryCommand is { } cmd && cmd.CanExecute(null))
            {
                cmd.Execute(null);
            }
        }

        // Abre el modal de Ajustes. Necesario por código (y no por Command)
        // porque el DataContext no llega a los MenuItem del MenuFlyout.
        private async void OnSettingsMenuClick(object? sender, RoutedEventArgs e)
        {
            await ShowSettingsAsync();
        }

        private async Task ShowSettingsAsync()
        {
            try
            {
                var dialog = new SettingsWindow { DataContext = ViewModel };
                await dialog.ShowDialog(this);
            }
            catch (Exception ex)
            {
                if (ViewModel is { } vm)
                {
                    vm.SystemStatus = ex.Message;
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
    }
}
