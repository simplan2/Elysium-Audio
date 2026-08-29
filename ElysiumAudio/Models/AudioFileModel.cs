using CommunityToolkit.Mvvm.ComponentModel;
using ElysiumAudio.ViewModels;
using System;
using System.Collections.Generic;
using System.Text;

namespace ElysiumAudio.Models
{
    public partial class AudioFileModel: ViewModelBase
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
        private string _status = "Pending";

        [ObservableProperty]
        private string _peak = "0.0 dBFS";

        [ObservableProperty]
        private string _loudness = "-0.0 LUFS";

        [ObservableProperty]
        private string _normalizedLoudness = "-0.0 LUFS";

        [ObservableProperty]
        private string _normalizedPeak = "0.0 dBFS";
    }
}
