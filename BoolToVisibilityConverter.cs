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
/// 布尔转透明度：true 为 1，false / null 为 0。
/// 用于分段分页的下划线 —— 不能交给 VisualState 的 Setter 去做：
/// WinUI 3 里 Setter 只会「加上」不会「撤回」，切走后下划线会一直留在旧分页上。
/// </summary>
public class BoolToOpacityConverter : IValueConverter
{
    public double TrueValue { get; set; } = 1;

    public double FalseValue { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && b ? TrueValue : FalseValue;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
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
