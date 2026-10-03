using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ElysiumAudio.ViewModels;
using System;
using System.Collections.Generic;
using System.Text;

namespace ElysiumAudio.Models
{
    public partial class AudioFileModel : ViewModelBase
    {
        [ObservableProperty]
        private string _fileName = string.Empty;

        [ObservableProperty]
        private string _filePath = string.Empty;

        [ObservableProperty]
        private string _codec = "-";

        [ObservableProperty]
        private string _duration = "00:00";

        [ObservableProperty]
        private AudioFileStatus _status = AudioFileStatus.Pending;

        [ObservableProperty]
        private string _statusMessage = "Pending";

        // Estas cuatro celdas se muestran bajo cabeceras que ya llevan la unidad
        // ("LUFS", "Pico dBTP"), y el motor devuelve el numero pelado: "-12.8", "0.22".
        // El default por tanto es "-" y no un "0.0 dBTP": con unidad, la unidad
        // aparecia al anadir archivos y desaparecia al medirlos. El "-" es ademas el
        // mismo marcador que ya usa el motor para "aun sin medir".
        [ObservableProperty]
        private string _peak = "-";

        [ObservableProperty]
        private string _loudness = "-";

        [ObservableProperty]
        private string _normalizedLoudness = "-";

        [ObservableProperty]
        private string _normalizedPeak = "-";

        // Valores numéricos de los resultados normalizados: se usan para colorear
        // las columnas del grid según cumplan o no el objetivo/techo configurado.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(NormLoudnessBrush))]
        [NotifyPropertyChangedFor(nameof(NormalizedDeviation))]
        [NotifyPropertyChangedFor(nameof(HasNormalizedResult))]
        [NotifyPropertyChangedFor(nameof(HasDisplayMetrics))]
        [NotifyPropertyChangedFor(nameof(DisplayLoudness))]
        [NotifyPropertyChangedFor(nameof(DisplayPeakDb))]
        [NotifyPropertyChangedFor(nameof(DisplayPlr))]
        private double _normalizedLoudnessDb = double.NaN;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(NormPeakBrush))]
        [NotifyPropertyChangedFor(nameof(DisplayPeakDb))]
        [NotifyPropertyChangedFor(nameof(DisplayPlr))]
        private double _normalizedPeakDb = double.NaN;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(NormLoudnessBrush))]
        [NotifyPropertyChangedFor(nameof(NormalizedDeviation))]
        private float _targetLufs = float.NaN;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(NormPeakBrush))]
        private float _ceilingDb = float.NaN;

        // Valores numéricos de la última medición (para resumen del lote y ficha del archivo).
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(InputLoudnessBrush))]
        [NotifyPropertyChangedFor(nameof(InputPeakBrush))]
        [NotifyPropertyChangedFor(nameof(HasDisplayMetrics))]
        [NotifyPropertyChangedFor(nameof(DisplayLoudness))]
        [NotifyPropertyChangedFor(nameof(DisplayPeakDb))]
        [NotifyPropertyChangedFor(nameof(DisplayPlr))]
        private bool _hasMeasurement;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayLoudness))]
        [NotifyPropertyChangedFor(nameof(DisplayPlr))]
        private float _integratedLoudness = -70f;

        // LRA (Loudness Range, EBU Tech 3342) del original y del resultado normalizado.
        // NaN cuando el material no da datos suficientes (clip más corto que la ventana
        // de 3 s, silencio digital o todo por debajo del suelo de -70 LUFS). No se
        // sustituye por 0 porque "no se puede calcular" y "cero de verdad" son cosas
        // distintas, y la ficha las presenta de forma distinta.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayLra))]
        private float _loudnessRange = float.NaN;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayLra))]
        private float _normalizedLoudnessRange = float.NaN;

        // LRA de la vista activa. Sigue al conmutador igual que el resto de métricas.
        public double DisplayLra => ShowNormalized ? NormalizedLoudnessRange : LoudnessRange;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(InputPeakDb))]
        [NotifyPropertyChangedFor(nameof(DisplayPeakDb))]
        [NotifyPropertyChangedFor(nameof(DisplayPlr))]
        [NotifyPropertyChangedFor(nameof(DisplayLra))]
        private float _maxTruePeakLinear;

        // Conmutador Original/Normalizado de la ficha. Vive en el modelo para que
        // cada archivo conserve su propia vista: no hay estado global que se pierda
        // al cambiar de selección.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasDisplayMetrics))]
        [NotifyPropertyChangedFor(nameof(DisplayLoudness))]
        [NotifyPropertyChangedFor(nameof(DisplayPeakDb))]
        [NotifyPropertyChangedFor(nameof(DisplayPlr))]
        [NotifyPropertyChangedFor(nameof(DisplayLra))]
        private bool _showNormalized;

        [ObservableProperty]
        private int _sampleRate;

        [ObservableProperty]
        private int _channels;

        // ── Métricas de la vista activa (original o normalizada) ──────────
        // Todas devuelven NaN cuando el conjunto pedido todavía no existe, de
        // forma que la ficha pueda distinguish "sin medir" de "cero de verdad".

        // Hay resultado normalizado medido. Es lo que habilita el conmutador.
        public bool HasNormalizedResult => !double.IsNaN(NormalizedLoudnessDb);

        // Pico real de entrada en dBFS. NaN en silencio digital.
        public double InputPeakDb => MaxTruePeakLinear > 0f
            ? 20.0 * Math.Log10(MaxTruePeakLinear)
            : double.NaN;

        public bool HasDisplayMetrics => ShowNormalized ? HasNormalizedResult : HasMeasurement;

        public double DisplayLoudness => ShowNormalized ? NormalizedLoudnessDb : IntegratedLoudness;

        public double DisplayPeakDb => ShowNormalized ? NormalizedPeakDb : InputPeakDb;

        // PLR de la vista activa. El umbral de -69.5 LUFS es el suelo de silencio
        // que aplica el motor: por debajo, pico y loudness dejan de guardar relación
        // y el PLR sería un número inventado.
        public double DisplayPlr
        {
            get
            {
                if (!HasDisplayMetrics) return double.NaN;
                double loudness = DisplayLoudness;
                double peak = DisplayPeakDb;
                if (double.IsNaN(loudness) || double.IsNaN(peak)) return double.NaN;
                if (loudness <= -69.5) return double.NaN;
                return peak - loudness;
            }
        }

        // ── Colores de las columnas del grid ─────────────────────────────
        // Mismas paletas del tema: skyblue para LUFS, teal para Peak, verde/ámbar/rojo
        // según cumplimiento de objetivo/techo en los resultados normalizados.
        private static readonly IBrush BrushMuted = Solid(0x5C, 0x6A, 0x82);   // TextMuted
        private static readonly IBrush BrushSky = Solid(0x5A, 0x92, 0xFA);     // AccentSkyblue
        private static readonly IBrush BrushTeal = Solid(0x2D, 0xD4, 0xBF);    // AccentTeal
        private static readonly IBrush BrushGreen = Solid(0x34, 0xD3, 0x99);   // Success
        private static readonly IBrush BrushAmber = Solid(0xFB, 0xBF, 0x24);   // Warning
        private static readonly IBrush BrushRed = Solid(0xF8, 0x71, 0x71);     // Error

        public IBrush InputLoudnessBrush => HasMeasurement ? BrushSky : BrushMuted;

        public IBrush InputPeakBrush => HasMeasurement ? BrushTeal : BrushMuted;

        // Umbrales de desviación respecto al objetivo. Son los mismos para la celda
        // del grid, la ficha del archivo y el resumen del lote: así el color significa
        // exactamente lo mismo en los tres sitios.
        public const double DeviationOk = 0.5;
        public const double DeviationWarn = 1.5;

        // Error de loudness tras normalizar, con signo: positivo = quedó más alto que
        // el objetivo, negativo = más bajo. NaN mientras no se haya normalizado.
        public double NormalizedDeviation =>
            double.IsNaN(NormalizedLoudnessDb) || float.IsNaN(TargetLufs)
                ? double.NaN
                : NormalizedLoudnessDb - TargetLufs;

        public static IBrush DeviationBrush(double absDeviation)
        {
            if (double.IsNaN(absDeviation)) return BrushMuted;
            if (absDeviation <= DeviationOk) return BrushGreen;
            return absDeviation <= DeviationWarn ? BrushAmber : BrushRed;
        }

        public IBrush NormLoudnessBrush => DeviationBrush(Math.Abs(NormalizedDeviation));

        public IBrush NormPeakBrush
        {
            get
            {
                if (double.IsNaN(NormalizedPeakDb) || float.IsNaN(CeilingDb)) return BrushMuted;
                return NormalizedPeakDb <= CeilingDb + 0.15 ? BrushGreen : BrushRed;
            }
        }

        private static IBrush Solid(int r, int g, int b) => new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b));

        //public string StatusIcon => Status switch
        //{
        //    AudioFileStatus.Pending => "⏳",
        //    AudioFileStatus.Analyzing => "🔍",
        //    AudioFileStatus.Processing => "⚙️",
        //    AudioFileStatus.Tagging => "📝",
        //    AudioFileStatus.Completed => "✅",
        //    AudioFileStatus.Error => "❌",
        //    _ => "⏳"
        //};

        //public string StatusColor => Status switch
        //{
        //    AudioFileStatus.Pending => "#71717A",
        //    AudioFileStatus.Analyzing => "#3B82F6",
        //    AudioFileStatus.Processing => "#F59E0B",
        //    AudioFileStatus.Tagging => "#8B5CF6",
        //    AudioFileStatus.Completed => "#10B981",
        //    AudioFileStatus.Error => "#EF4444",
        //    _ => "#71717A"
        //};

        //partial void OnStatusChanged(AudioFileStatus value)
        //{
        //    OnPropertyChanged(nameof(StatusIcon));
        //    OnPropertyChanged(nameof(StatusColor));
        //}
    }

    public enum AudioFileStatus
    {
        Pending,
        Analyzing,
        Processing,
        Tagging,
        Completed,
        Error
    }
}
