using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AStation_Client.Models;
using AStation_Client.Services;
using AStation_Client.ViewModels;

namespace AStation_Client.Pages;

public sealed partial class ChannelPage : Page
{
    public ObservableCollection<ChannelItem> Channels { get; } = new();

    public ChannelPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await LoadChannelsAsync();
    }

    /// <summary>
    /// 从 AcFun 接口获取频道导航列表
    /// GET /rest/pc-direct/page/queryNavigators
    /// </summary>
    private async Task LoadChannelsAsync()
    {
        try
        {
            var response = await ServiceLocator.Api.GetAsync<NavigatorsResponse>("/rest/pc-direct/page/queryNavigators");

            if (response?.Result != 0 || response.Data == null)
            {
                LoadDefaultChannels();
                return;
            }

            Channels.Clear();
            foreach (var nav in response.Data)
            {
                if (string.IsNullOrWhiteSpace(nav.NavName)) continue;
                Channels.Add(new ChannelItem
                {
                    Name = nav.NavName,
                    Tag = nav.Link ?? nav.Cid.ToString()
                });
            }

            if (Channels.Count == 0)
            {
                LoadDefaultChannels();
            }
        }
        catch
        {
            LoadDefaultChannels();
        }

        ChannelGrid.ItemsSource = Channels;
    }

    private void LoadDefaultChannels()
    {
        Channels.Clear();
        Channels.Add(new ChannelItem { Name = "全部视频", Tag = "all" });
        Channels.Add(new ChannelItem { Name = "游戏", Tag = "game" });
        Channels.Add(new ChannelItem { Name = "舞蹈", Tag = "dance" });
        Channels.Add(new ChannelItem { Name = "音乐", Tag = "music" });
        Channels.Add(new ChannelItem { Name = "科技", Tag = "tech" });
        Channels.Add(new ChannelItem { Name = "生活", Tag = "life" });
        Channels.Add(new ChannelItem { Name = "影视", Tag = "film" });
        Channels.Add(new ChannelItem { Name = "体育", Tag = "sport" });
        Channels.Add(new ChannelItem { Name = "搞笑", Tag = "funny" });
    }
}
