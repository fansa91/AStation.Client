using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AStation_Client.Pages;

public sealed partial class AboutPage : Page
{
    private const string RepoUrl = "https://github.com/fansa91/AStation.Client";

    public AboutPage()
    {
        InitializeComponent();

        var version = typeof(AboutPage).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(AboutPage).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        // 去掉 SDK 可能追加的 +commit 尾巴
        int plus = version.IndexOf('+');
        if (plus > 0) version = version[..plus];

        VersionText.Text = $"版本 {version} · WinUI 3 / .NET 10";
    }

    private async void RepoButton_Click(object sender, RoutedEventArgs e)
    {
        await Windows.System.Launcher.LaunchUriAsync(new Uri(RepoUrl));
    }

    private async void IssuesButton_Click(object sender, RoutedEventArgs e)
    {
        await Windows.System.Launcher.LaunchUriAsync(new Uri($"{RepoUrl}/issues"));
    }
}
