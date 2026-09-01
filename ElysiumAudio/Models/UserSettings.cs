namespace ElysiumAudio.Models
{
    /// <summary>
    /// Modelo de datos con las preferencias editables del usuario.
    /// Solo contiene datos, sin lógica de persistencia (eso lo hace ISettingsService).
    /// </summary>
    public class UserSettings
    {
        public double TargetLufs { get; set; } = Helpers.DefaultValues.DEFAULT_TARGET_LUFS;
        public double TruePeakCeiling { get; set; } = Helpers.DefaultValues.DEFAULT_TRUE_PEAK_CEILING;
        public double ReleaseTimeMs { get; set; } = Helpers.DefaultValues.DEFAULT_RELEASE_TIME_MS;
        public double LookAheadTimeMs { get; set; } = Helpers.DefaultValues.DEFAULT_LOOK_AHEAD_TIME_MS;
        public string OutputDirectory { get; set; } = string.Empty;
    }
}
