using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Layal.Services;

/// <summary>
/// Chooses a text run's reading direction and alignment without reversing the surrounding WPF layout.
/// </summary>
public sealed class TextDirectionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isRightToLeft = value switch
        {
            FlowDirection direction => direction == FlowDirection.RightToLeft,
            string text => ContainsRightToLeftLetter(text),
            _ => false
        };

        if (targetType == typeof(TextAlignment))
            return isRightToLeft ? TextAlignment.Right : TextAlignment.Left;

        return isRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static bool ContainsRightToLeftLetter(string value)
    {
        foreach (var character in value)
        {
            // Hebrew, Arabic, and related RTL scripts should start from the right.
            if (char.IsLetter(character) && character is >= '\u0590' and <= '\u08FF')
                return true;
        }

        return false;
    }
}
