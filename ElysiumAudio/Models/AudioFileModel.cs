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

        [ObservableProperty]
        private string _peak = "0.0 dBFS";

        [ObservableProperty]
        private string _loudness = "-0.0 LUFS";

        [ObservableProperty]
        private string _normalizedLoudness = "-0.0 LUFS";

        [ObservableProperty]
        private string _normalizedPeak = "0.0 dBFS";

        // Valores numéricos de los resultados normalizados: se usan para colorear
        // las columnas del grid según cumplan o no el objetivo/techo configurado.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(NormLoudnessBrush))]
        private double _normalizedLoudnessDb = double.NaN;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(NormPeakBrush))]
        private double _normalizedPeakDb = double.NaN;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(NormLoudnessBrush))]
        private float _targetLufs = float.NaN;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(NormPeakBrush))]
        private float _ceilingDb = float.NaN;

        // Valores numéricos de la última medición (para resumen del lote y ficha del archivo).
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(InputLoudnessBrush))]
        [NotifyPropertyChangedFor(nameof(InputPeakBrush))]
        private bool _hasMeasurement;

        [ObservableProperty]
        private float _integratedLoudness = -70f;

        [ObservableProperty]
        private float _maxTruePeakLinear;

        [ObservableProperty]
        private int _sampleRate;

        [ObservableProperty]
        private int _channels;

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

        public IBrush NormLoudnessBrush
        {
            get
            {
                if (double.IsNaN(NormalizedLoudnessDb)) return BrushMuted;
                double diff = Math.Abs(NormalizedLoudnessDb - TargetLufs);
                if (diff <= 0.5) return BrushGreen;
                if (diff <= 1.5) return BrushAmber;
                return BrushRed;
            }
        }

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
