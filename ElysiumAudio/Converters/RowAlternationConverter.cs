using Avalonia;
using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace ElysiumAudio.Converters
{
    /// <summary>
    /// Devuelve el tono de superficie alternado (zebra striping) del DataGrid
    /// según el índice de la fila, reutilizando los brushes del design system.
    /// </summary>
    public class RowAlternationConverter : IValueConverter
    {
        private const string EvenRowKey = "BrushBgPanel";
        private const string OddRowKey = "BrushBgPanelAlt";

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            int rowIndex = value is int index ? index : 0;
            string key = rowIndex % 2 == 0 ? EvenRowKey : OddRowKey;

            if (Application.Current is { } app
                && app.Resources.TryGetResource(key, app.ActualThemeVariant, out object? resource))
                return resource;

            return null;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}