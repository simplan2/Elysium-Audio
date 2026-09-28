using CommunityToolkit.Mvvm.ComponentModel;

namespace ElysiumAudio.Models
{
    /// <summary>
    /// Configuración predefinida de normalización para plataformas de streaming.
    /// Name es observable para poder seguir al idioma activo.
    /// </summary>
    public partial class NormalizationPreset : ObservableObject
    {
        [ObservableProperty]
        private string _name = "";

        /// <summary>Clave de traducción de Name; vacía si el nombre es una marca (p. ej. Spotify).</summary>
        public string NameKey { get; init; } = "";

        public bool IsCustom { get; init; }
        public double TargetLufs { get; init; }
        public double TruePeakCeiling { get; init; }
        public double ReleaseTimeMs { get; init; }
        public double LookAheadTimeMs { get; init; }
    }
}
