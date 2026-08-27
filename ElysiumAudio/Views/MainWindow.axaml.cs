using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ElysiumAudio.ViewModels;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ElysiumAudio.Views
{
    public partial class MainWindow : Window
    {
     
        // Propiedad segura que extrae el ViewModel real que App.axaml.cs le inyectó a la ventana
        private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

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

        private async void OnDrop(object? sender, DragEventArgs e)
        {
            if (e.DataTransfer.Formats.Contains(DataFormat.File))
            {
                var files = e.DataTransfer.TryGetFiles();
                if (files != null && ViewModel != null)
                {
                    var validPaths = new List<string>();
                    foreach (var file in files)
                    {
                        string path = file.Path.LocalPath;
                        string ext = Path.GetExtension(path).ToLower();
                        if (ext == ".wav" || ext == ".flac")
                        {
                            validPaths.Add(path);
                        }
                    }

                    if (validPaths.Count == 0) return;

                    ViewModel.SystemStatus = $"Cargando {validPaths.Count} archivo(s)...";

                    await Task.Run(() =>
                    {
                        foreach (var path in validPaths)
                        {
                            try
                            {
                                using (var reader = new WaveFileReader(path))
                                {
                                    var fileInfo = new FileInfo(path);
                                    TimeSpan duration = reader.TotalTime;
                                    string durationFormatted = duration.ToString(duration.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");

                                    var audioModel = new Models.AudioFileModel
                                    {
                                        FilePath = path,
                                        FileName = fileInfo.Name,
                                        Codec = Path.GetExtension(path).ToUpper().Replace(".", ""),
                                        Duration = durationFormatted,
                                        Status = "Pending",
                                        Peak = "—",
                                        Loudness = "—"
                                    };

                                    // Corrección del Dispatcher estático
                                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                    {
                                        ViewModel.AudioFiles.Add(audioModel);
                                    });
                                }
                            }
                            catch { /* Ignorar archivos corruptos */ }
                        }
                    });

                    ViewModel.SystemStatus = $"¡{validPaths.Count} archivo(s) listos en la cola!";
                }
            }
        }
    }
}