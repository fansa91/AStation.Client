using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AStation_Client.Controls;

/// <summary>
/// 统一状态面板：Loading / Empty / Error 三态。
/// 叠在内容区之上（Grid 覆盖层），显示与隐藏都不占用布局空间，因此不会把内容顶动。
/// </summary>
public sealed partial class StatePanel : UserControl
{
    public StatePanel()
    {
        InitializeComponent();
    }

    /// <summary>重试 / 行动按钮被点击</summary>
    public event EventHandler? ActionClick;

    /// <summary>加载中：环形进度 + 可选说明</summary>
    public void ShowLoading(string? description = null)
    {
        Show();
        Ring.Visibility = Visibility.Visible;
        Ring.IsActive = true;
        StateIcon.Visibility = Visibility.Collapsed;
        TitleText.Text = "加载中…";
        SetDescription(description);
        ActionButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>空数据</summary>
    public void ShowEmpty(
        string title = "暂无内容",
        string? description = null,
        string? actionText = null,
        string glyph = "\uE7BA")
    {
        ShowStatic(glyph, title, description, actionText);
    }

    /// <summary>请求失败：默认给一个「重试」按钮</summary>
    public void ShowError(
        string title = "加载失败",
        string? description = "网络或接口异常，请稍后重试。",
        string? actionText = "重试",
        string glyph = "\uEA39")
    {
        ShowStatic(glyph, title, description, actionText);
    }

    /// <summary>隐藏，露出内容</summary>
    public void Hide()
    {
        Root.Visibility = Visibility.Collapsed;
        Ring.IsActive = false;
    }

    /// <summary>是否正在显示</summary>
    public bool IsShowing => Root.Visibility == Visibility.Visible;

    private void ShowStatic(string glyph, string title, string? description, string? actionText)
    {
        Show();
        Ring.IsActive = false;
        Ring.Visibility = Visibility.Collapsed;
        StateIcon.Glyph = glyph;
        StateIcon.Visibility = Visibility.Visible;
        TitleText.Text = title;
        SetDescription(description);

        ActionButton.Content = actionText ?? string.Empty;
        ActionButton.Visibility = string.IsNullOrWhiteSpace(actionText)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void Show()
    {
        Root.Visibility = Visibility.Visible;
    }

    private void SetDescription(string? description)
    {
        DescText.Text = description ?? string.Empty;
        DescText.Visibility = string.IsNullOrWhiteSpace(description)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        ActionClick?.Invoke(this, EventArgs.Empty);
    }
}
