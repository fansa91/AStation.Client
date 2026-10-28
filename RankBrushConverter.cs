using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AStation_Client;

/// <summary>
/// 排行榜名次 → 徽章画刷。
/// 前三名用醒目配色，其余用中性灰。
/// 绑定值可为 int 或 "1"/"2" 这样的字符串；ConverterParameter="Foreground" 取文字色。
/// </summary>
public class RankBrushConverter : IValueConverter
{
    private static readonly Color GoldColor = Color.FromArgb(255, 0xF0, 0x6A, 0x28);
    private static readonly Color SilverColor = Color.FromArgb(255, 0xF2, 0xA8, 0x33);
    private static readonly Color BronzeColor = Color.FromArgb(255, 0x8C, 0x7B, 0xE8);
    private static readonly Color WhiteColor = Color.FromArgb(255, 255, 255, 255);

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        int rank = ParseRank(value);
        bool foreground = parameter is string mode
                          && string.Equals(mode, "Foreground", StringComparison.OrdinalIgnoreCase);

        if (rank is 1 or 2 or 3)
        {
            if (foreground)
            {
                return new SolidColorBrush(WhiteColor);
            }

            return new SolidColorBrush(rank switch
            {
                1 => GoldColor,
                2 => SilverColor,
                _ => BronzeColor
            });
        }

        // 非前三名：背景用低对比度灰，文字用次要色
        return foreground
            ? new SolidColorBrush(Color.FromArgb(255, 0x8A, 0x8A, 0x8A))
            : new SolidColorBrush(Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();

    private static int ParseRank(object? value)
    {
        return value switch
        {
            int i => i,
            string s when int.TryParse(s, out int parsed) => parsed,
            _ => 0
        };
    }
}
