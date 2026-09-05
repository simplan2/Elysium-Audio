using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace ElysiumAudio.Converters
{
    public class DynamicNumberFormatConverter : IValueConverter
    {

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is double d)
            {
                // Si es entero, mostrar sin decimales
                if (Math.Abs(d % 1) < double.Epsilon)
                    return d.ToString("F0", culture);

                // Si tiene decimales, mostrar con 1 decimal
                return d.ToString("F1", culture);
            }
            return value;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (double.TryParse(value?.ToString(), NumberStyles.Float, culture, out double result))
                return result;

            return 0.0;
        }
    }
}
