using Avalonia.Data.Converters;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ElysiumAudio.Converters
{
    public class TextCaseConverter : IValueConverter
    {
        //public static readonly TextCaseConverter Instance = new();
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string sourceText && parameter is string targetCase
            && targetType.IsAssignableTo(typeof(string)))
            {
                switch (targetCase)
                {
                    case "upper":
                        return sourceText.ToUpperInvariant();
                    default:
                        return sourceText;
                }
            }
            return value;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
