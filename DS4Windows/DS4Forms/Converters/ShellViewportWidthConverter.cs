using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DS4WinWPF.DS4Forms.Converters
{
    /// <summary>
    /// Converts the main window width into the usable shell content width.
    /// The bridge shell reserves a fixed sidebar and symmetric content margins;
    /// explicitly constraining scrollable feature pages prevents their star-sized
    /// cards from being measured at an effectively infinite width by ScrollViewer.
    /// </summary>
    public sealed class ShellViewportWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not double windowWidth || double.IsNaN(windowWidth) || windowWidth <= 0)
            {
                return DependencyProperty.UnsetValue;
            }

            double reservedWidth = 318.0;
            if (parameter is string text &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                reservedWidth = parsed;
            }

            return Math.Max(496.0, windowWidth - reservedWidth);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
