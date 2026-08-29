using Avalonia.Data.Converters;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ElysiumAudio.Converters
{
    public class LufsRangeConverter: IValueConverter
    {
        private const double MinLufs = -24.0;
        private const double MaxLufs = -6.0;

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is double d)
            {
                // Siempre mostrar con 1 decimal
                return d.ToString("F1", CultureInfo.InvariantCulture);
            }
            return "-14.0";
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            {
                // Clamp al rango permitido
                if (result < MinLufs) result = MinLufs;
                else if (result > MaxLufs) result = MaxLufs;

                // Redondear a 1 decimal
                return Math.Round(result, 1);
            }

            // Si no es válido, devolver el valor por defecto
            return -14.0;
        }
    }
}
