using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Evania.Converters
{
    public class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, System.Type targetType, object parameter, string language)
        {
            return value is true ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, System.Type targetType, object parameter, string language)
        {
            return value is not Visibility.Visible;
        }
    }
}
