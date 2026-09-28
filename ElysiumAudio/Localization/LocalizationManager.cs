using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;

namespace ElysiumAudio.Localization
{
    /// <summary>
    /// Administrador de idioma global (singleton). Entrega a LocalizeExtension un
    /// objeto por clave (TranslationEntry) que expone el texto actual y notifica
    /// los cambios: así, al cambiar de idioma, cada binding ya enlazado se
    /// actualiza en vivo. LanguageChanged permite a los ViewModels y al
    /// code-behind recalcular sus propios textos.
    /// </summary>
    public sealed class LocalizationManager : ObservableObject
    {
        public static LocalizationManager Instance { get; } = new();

        public event EventHandler? LanguageChanged;

        private readonly Dictionary<string, TranslationEntry> _entries = new();
        private IReadOnlyDictionary<string, string> _current = Translations.Spanish;
        private string _language = "es";

        private LocalizationManager() { }

        public string Language => _language;

        public bool IsEnglish => _language == "en";

        public IReadOnlyDictionary<string, string> Current => _current;

        public string this[string key] => Get(key);

        public string Get(string key) => _current.TryGetValue(key, out var value) ? value : key;

        /// <summary>
        /// Indica si un texto corresponde a la traducción de una clave en cualquiera
        /// de los idiomas disponibles. Sirve para reconocer textos ya traducidos que
        /// hay que volver a generar tras un cambio de idioma.
        /// </summary>
        public static bool IsTextOfKey(string value, string key)
            => (Translations.Spanish.TryGetValue(key, out var es) && es == value)
            || (Translations.English.TryGetValue(key, out var en) && en == value);

        // Objeto enlazable de una clave (cacheado). LocalizeExtension lo usa como
        // Source del binding para que el cambio de idioma refresque el texto.
        internal TranslationEntry GetEntry(string key)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new TranslationEntry(key);
                _entries[key] = entry;
            }

            return entry;
        }

        public void SetLanguage(string? language)
        {
            string lang = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "es";
            if (lang == _language) return;

            _language = lang;
            _current = lang == "en" ? Translations.English : Translations.Spanish;

            // Notifica a todos los bindings de texto ya enlazados.
            foreach (var entry in _entries.Values)
            {
                entry.Refresh();
            }

            OnPropertyChanged(string.Empty);
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Texto de una clave, observable. Lo consume LocalizeExtension.
    /// </summary>
    public sealed class TranslationEntry : ObservableObject
    {
        private readonly string _key;

        internal TranslationEntry(string key)
        {
            _key = key;
        }

        public string Value => LocalizationManager.Instance.Get(_key);

        internal void Refresh() => OnPropertyChanged(nameof(Value));
    }
}