using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;

namespace AStation_Client;

/// <summary>
/// 把字符串 URL（http/https/ms-appx/相对路径）转换为 ImageSource。
/// x:Bind 无法把 string 隐式转换为 ImageSource，因此需要这个转换器。
/// </summary>
public class StringToImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        var url = value as string;
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        try
        {
            if (Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri))
            {
                return new BitmapImage { UriSource = uri };
            }
        }
        catch
        {
            // 非法 URL 时返回 null，图片区域留空
        }

        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
