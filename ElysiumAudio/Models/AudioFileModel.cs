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
