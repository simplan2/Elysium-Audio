using ElysiumAudio.Models;
using System;
using System.IO;
using System.Text.Json;

namespace ElysiumAudio.Services
{
    /// <summary>
    /// Implementación de ISettingsService que persiste la configuración del usuario en un archivo JSON.
    /// </summary>
    public class SettingsService : ISettingsService
    {
        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ElysiumAudio");

        private static readonly string ConfigPath = Path.Combine(ConfigDir, "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public UserSettings Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var loaded = JsonSerializer.Deserialize<UserSettings>(json, JsonOptions);
                    if (loaded != null) return loaded;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error cargando configuración: {ex.Message}");
            }

            return new UserSettings();
        }

        public void Save(UserSettings settings)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                string json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error guardando configuración: {ex.Message}");
            }
        }
    }
}
