using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using ElysiumAudio.Services;
using ElysiumAudio.ViewModels;
using ElysiumAudio.Views;
using Microsoft.Extensions.DependencyInjection;
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
                var services = new ServiceCollection();

                // Aquí puedes registrar otros servicios si es necesario
                services.AddSingleton<ISettingsService, SettingsService>();
                // ViewModels
                services.AddSingleton<MainWindowViewModel>();

                var provider = services.BuildServiceProvider();


                desktop.MainWindow = new MainWindow
                {
                    DataContext = provider.GetService<MainWindowViewModel>(),
                };

                // Guardar las preferencias al cerrar la ventana (cierre con 'X'),
                // y como salvaguarda también al salir completamente de la aplicación.
                desktop.MainWindow.Closing += (_, _) =>
                {
                    provider.GetService<ISettingsService>()?.Save();
                };

                desktop.Exit += (sender, e) =>
                {
                    var settingsService = provider.GetService<ISettingsService>();
                    settingsService?.Save();
                };
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
