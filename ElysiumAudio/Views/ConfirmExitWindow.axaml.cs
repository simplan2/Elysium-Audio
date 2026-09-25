using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ElysiumAudio.Views
{
    // Diálogo de confirmación al cerrar la app durante un procesamiento.
    // Close(false) = seguir procesando (No), Close(true) = cancelar y salir (Sí).
    public partial class ConfirmExitWindow : Window
    {
        public ConfirmExitWindow()
        {
            InitializeComponent();
        }

        private void OnContinueClick(object? sender, RoutedEventArgs e) => Close(false);

        private void OnExitClick(object? sender, RoutedEventArgs e) => Close(true);
    }
}