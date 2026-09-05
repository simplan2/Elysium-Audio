using ElysiumAudio.Models;
using System;
using System.ComponentModel;

namespace ElysiumAudio.Services
{
    public interface ISettingsService
    {

        UserSettings Current { get; }
        event EventHandler<PropertyChangedEventArgs>? SettingsChanged;
        void Save();
        void Load();        
    }
}
