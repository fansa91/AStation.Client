using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AStation_Client.Services;

namespace AStation_Client.Pages.Overlay;

public sealed partial class UserPage : Page
{
    public UserPage()
    {
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        bool loggedIn = AccountState.IsLoggedIn;

        UserNameText.Text = loggedIn && !string.IsNullOrWhiteSpace(AccountState.UserName)
            ? AccountState.UserName!
            : (loggedIn ? "已登录用户" : "未登录");

        StatusText.Text = loggedIn
            ? "账号已登录 · 收藏与追番将使用该账号"
            : "登录后可以查看自己的收藏与追番";

        LogoutButton.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;

        Avatar.DisplayName = AccountState.UserName ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(AccountState.AvatarUrl)
            && Uri.TryCreate(AccountState.AvatarUrl, UriKind.Absolute, out var uri))
        {
            Avatar.ProfilePicture = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
        }

        WorksState.ShowEmpty(
            loggedIn ? "作品列表暂未接入" : "还没有登录",
            loggedIn
                ? "账号信息已就绪，投稿 / 收藏列表的接口还在接入中，先看首页推荐吧。"
                : "登录后这里会显示你的投稿与收藏。",
            actionText: loggedIn ? null : "去登录",
            glyph: loggedIn ? "\uE8F1" : "\uE77B");
    }

    private void WorksState_ActionClick(object? sender, EventArgs e)
    {
        if (AccountState.IsLoggedIn) return;

        if (App.MainWindowInstance is MainWindow window)
        {
            window.OpenOverlay(typeof(LoginPage));
        }
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        AccountState.Clear();
        Refresh();
    }
}
