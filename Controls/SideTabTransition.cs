using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace AStation_Client.Controls;

/// <summary>
/// 详情页右栏分页切换的入场过渡（淡入 + 轻微上移）。
///
/// 沿用 PlayerOverlay 控制层的做法：Storyboard 同时动 Opacity 与 TranslateTransform.Y。
/// 位移的 TranslateTransform 必须在 XAML 里具名声明 —— WinUI 3 用 Storyboard
/// 动代码里新建的 Transform 不会真正插值（同 DanmakuPanel 的说明）。
/// </summary>
public static class SideTabTransition
{
    private const int DurationMs = 180;

    /// <summary>入场位移距离（物理上就是几个像素的抬头感，别做成翻页）</summary>
    private const double SlideOffset = 10;

    public static void PlayIn(UIElement panel, TranslateTransform shift)
    {
        panel.Opacity = 1;
        shift.Y = 0;

        var duration = new Duration(TimeSpan.FromMilliseconds(DurationMs));
        var storyboard = new Storyboard();

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = duration,
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, panel);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);

        var slide = new DoubleAnimation
        {
            From = SlideOffset,
            To = 0,
            Duration = duration,
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(slide, shift);
        Storyboard.SetTargetProperty(slide, "Y");
        storyboard.Children.Add(slide);

        storyboard.Begin();
    }
}
