using Avalonia.Data;
using Avalonia.Markup.Xaml;
using System;

namespace ElysiumAudio.Localization
{
    /// <summary>
    /// Se usa en XAML: Text="{l10n:Localize AddFiles}". Devuelve un binding a la
    /// entrada de la clave, que se actualiza en vivo al cambiar de idioma.
    /// </summary>
    public class LocalizeExtension : MarkupExtension
    {
        public LocalizeExtension() { }

        public LocalizeExtension(string key)
        {
            Key = key;
        }

        public string? Key { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            if (string.IsNullOrEmpty(Key))
            {
                return string.Empty;
            }

            return new Binding(nameof(TranslationEntry.Value))
            {
                Source = LocalizationManager.Instance.GetEntry(Key),
                Mode = BindingMode.OneWay
            };
        }
    }
}
