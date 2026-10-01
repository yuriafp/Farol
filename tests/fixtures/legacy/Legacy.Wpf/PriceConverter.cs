using System;
using System.Globalization;
using System.Windows.Data;

namespace Legacy.Wpf
{
    // Declared only in MainWindow.xaml (Window.Resources): invisible to a C#-only reference search.
    public class PriceConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is decimal price ? price.ToString("C", culture) : value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
