using Avalonia.Controls;
using Avalonia.Interactivity;
using ElysiumAudio.Localization;
using System;

namespace ElysiumAudio.Views
{
    // Modal de Ajustes: idioma, carpeta de salida e información del producto.
    // El DataContext es el MainWindowViewModel del singleton, así que los cambios
    // de idioma y de carpeta se aplican en vivo y se guardan solos.
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            RefreshTitle();
            LocalizationManager.Instance.LanguageChanged += OnLanguageChanged;
            Closed += (_, _) => LocalizationManager.Instance.LanguageChanged -= OnLanguageChanged;
        }

        // Window.Title no admite binding, así que se actualiza a mano.
        private void RefreshTitle() => Title = LocalizationManager.Instance.Get("SettingsTitle");

        private void OnLanguageChanged(object? sender, EventArgs e) => RefreshTitle();

        private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
    }
}
