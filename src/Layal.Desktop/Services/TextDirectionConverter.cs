using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Layal.Services;

/// <summary>
/// Chooses a text run's reading direction without reversing the surrounding WPF layout.
/// </summary>
public sealed class TextDirectionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string text)
            return FlowDirection.LeftToRight;

        foreach (var character in text)
        {
            // Hebrew, Arabic, and the supplementary RTL scripts should start from the right.
            if (char.IsLetter(character) && character is >= '\u0590' and <= '\u08FF')
                return FlowDirection.RightToLeft;
        }

        return FlowDirection.LeftToRight;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
