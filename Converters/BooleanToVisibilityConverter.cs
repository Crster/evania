using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Evania.Converters
{
    public class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, System.Type targetType, object parameter, string language)
        {
            return value is true ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, System.Type targetType, object parameter, string language)
        {
            return value is Visibility.Visible;
        }
    }
}