namespace ElysiumAudio.Models
{
    /// <summary>
    /// Configuración predefinida de normalización para plataformas de streaming.
    /// </summary>
    public class NormalizationPreset
    {
        public string Name { get; init; } = "";
        public bool IsCustom { get; init; }
        public double TargetLufs { get; init; }
        public double TruePeakCeiling { get; init; }
        public double ReleaseTimeMs { get; init; }
        public double LookAheadTimeMs { get; init; }
    }
}