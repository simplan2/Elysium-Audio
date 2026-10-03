using ElysiumAudio.Models;
using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace ElysiumAudio.Services
{
    /// <summary>
    /// Implementación de ISettingsService que persiste la configuración del usuario en un archivo JSON.
    /// </summary>
    public class SettingsService : ISettingsService
    {
        public UserSettings Current { get; set; } = new UserSettings();

        public event EventHandler<PropertyChangedEventArgs>? SettingsChanged;

        public SettingsService()
        {
            Load();
        }

        private void OnSettingPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            SettingsChanged?.Invoke(this, e);
        }

        public void Load()
        {
            try
            {
                // Desuscribirse del evento PropertyChanged del objeto Current anterior
                if (Current != null)
                    Detach(Current);

                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var loaded = JsonSerializer.Deserialize<UserSettings>(json, JsonOptions)
                                 ?? new UserSettings();

                    // Migración: en un JSON anterior a la existencia de
                    // PreserveMetadata la clave no está, y el bool deserializa a
                    // false, lo que desactivaría el clonado de metadatos sin que el
                    // usuario lo haya pedido. Si la clave no viene, se usa el
                    // default (true).
                    if (!HasKey(json, nameof(UserSettings.PreserveMetadata)))
                    {
                        loaded.PreserveMetadata = true;
                    }

                    Current = ClampToRanges(loaded);
                }
                else
                {
                    Current = new UserSettings();
                }

                Attach(Current!);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error cargando configuración: {ex.Message}");
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                string json = JsonSerializer.Serialize(Current, JsonOptions);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error guardando configuración: {ex.Message}");
            }
        }

        private static readonly string ConfigDir = Path.Combine(
           Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
           "ElysiumAudio");

        private static readonly string ConfigPath = Path.Combine(ConfigDir, "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        /// <summary>Indica si el JSON guardado contiene la propiedad indicada.</summary>
        private static bool HasKey(string json, string propertyName)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                       && doc.RootElement.TryGetProperty(propertyName, out _);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private void Attach(UserSettings settings)
        {
            settings.PropertyChanged += OnSettingPropertyChanged;
        }

        private void Detach(UserSettings settings)
        {
            settings.PropertyChanged -= OnSettingPropertyChanged;
        }

        // Aplica los límites mínimos/máximos a los valores cargados desde disco,
        // evitando que un JSON corrupto o antiguo cargue valores fuera de rango.
        private static UserSettings ClampToRanges(UserSettings settings)
        {
            settings.TargetLufs = Math.Clamp(settings.TargetLufs,
                Helpers.DefaultValues.MIN_TARGET_LUFS, Helpers.DefaultValues.MAX_TARGET_LUFS);
            settings.TruePeakCeiling = Math.Clamp(settings.TruePeakCeiling,
                Helpers.DefaultValues.MIN_TRUE_PEAK_CEILING, Helpers.DefaultValues.MAX_TRUE_PEAK_CEILING);
            settings.ReleaseTimeMs = Math.Clamp(settings.ReleaseTimeMs,
                Helpers.DefaultValues.MIN_RELEASE_TIME_MS, Helpers.DefaultValues.MAX_RELEASE_TIME_MS);
            settings.LookAheadTimeMs = Math.Clamp(settings.LookAheadTimeMs,
                Helpers.DefaultValues.MIN_LOOK_AHEAD_TIME_MS, Helpers.DefaultValues.MAX_LOOK_AHEAD_TIME_MS);
            settings.AppMode = Enum.IsDefined(typeof(AppMode), settings.AppMode)
                ? settings.AppMode
                : AppMode.Normalize;
            settings.Language = string.Equals(settings.Language, "en", StringComparison.OrdinalIgnoreCase)
                ? "en"
                : "es";
            return settings;
        }

    }
}
