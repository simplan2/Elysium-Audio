using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using ElysiumAudio.Services;
using ElysiumAudio.ViewModels;
using ElysiumAudio.Views;
using System.Linq;

namespace ElysiumAudio
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Creamos e inyectamos el servicio de configuración
                var settingsService = new SettingsService();
                var viewModel = new MainWindowViewModel(settingsService);

                desktop.MainWindow = new MainWindow
                {
                    DataContext = viewModel,
                };

                // Guardar todas las preferencias del usuario al cerrar la app
                desktop.MainWindow.Closing += (_, _) => viewModel.SaveSettings();
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
