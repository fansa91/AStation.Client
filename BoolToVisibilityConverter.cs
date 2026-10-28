using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace AStation_Client;

public class BoolToVisibilityConverter : IValueConverter
{
    public bool IsReverse { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool b = value is bool boolVal && boolVal;
        if (IsReverse) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 对象转可见性：null 或空字符串为 Collapsed，否则 Visible
/// </summary>
public class ObjectToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value == null) return Visibility.Collapsed;
        if (value is string str && string.IsNullOrWhiteSpace(str)) return Visibility.Collapsed;
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
