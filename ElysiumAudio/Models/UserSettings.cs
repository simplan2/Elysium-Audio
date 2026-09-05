using CommunityToolkit.Mvvm.ComponentModel;
using ElysiumAudio.ViewModels;

namespace ElysiumAudio.Models
{
    /// <summary>
    /// Modelo de datos con las preferencias editables del usuario.
    /// Solo contiene datos, sin lógica de persistencia (eso lo hace ISettingsService).
    /// </summary>
    public partial class UserSettings: ViewModelBase
    {
        [ObservableProperty]
        private double targetLufs = Helpers.DefaultValues.DEFAULT_TARGET_LUFS;

        [ObservableProperty]
        private double truePeakCeiling = Helpers.DefaultValues.DEFAULT_TRUE_PEAK_CEILING;

        [ObservableProperty]
        private double releaseTimeMs = Helpers.DefaultValues.DEFAULT_RELEASE_TIME_MS;

        [ObservableProperty]
        private double lookAheadTimeMs = Helpers.DefaultValues.DEFAULT_LOOK_AHEAD_TIME_MS;

        [ObservableProperty]
        private string outputDirectory = string.Empty;
    }
}
