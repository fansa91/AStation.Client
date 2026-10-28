using Microsoft.UI.Xaml.Controls;
using AStation_Client.ViewModels;

namespace AStation_Client.Pages;

public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadDataAsync();
    }

    private void RetryButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _ = ViewModel.LoadDataAsync();
    }

    /// <summary>
    /// 视频卡片点击：导航到 VideoPage 并传递 resourceId
    /// </summary>
    private void VideoCard_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Controls.VideoCardControl card && !string.IsNullOrWhiteSpace(card.ResourceId))
        {
            Frame.Navigate(typeof(Pages.VideoPage), card.ResourceId);
        }
    }
}
