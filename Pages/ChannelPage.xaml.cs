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
                    Tag = nav.Link ?? nav.Cid.ToString(),
                    Id = nav.Cid
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
        Channels.Add(new ChannelItem { Name = "全部视频", Tag = "all", Id = 1 });
        Channels.Add(new ChannelItem { Name = "游戏", Tag = "game", Id = 2 });
        Channels.Add(new ChannelItem { Name = "舞蹈", Tag = "dance", Id = 3 });
        Channels.Add(new ChannelItem { Name = "音乐", Tag = "music", Id = 4 });
        Channels.Add(new ChannelItem { Name = "科技", Tag = "tech", Id = 5 });
        Channels.Add(new ChannelItem { Name = "生活", Tag = "life", Id = 6 });
        Channels.Add(new ChannelItem { Name = "影视", Tag = "film", Id = 7 });
        Channels.Add(new ChannelItem { Name = "体育", Tag = "sport", Id = 8 });
        Channels.Add(new ChannelItem { Name = "搞笑", Tag = "funny", Id = 9 });
    }

    /// <summary>分区卡片 → 该分区的排行榜（cid 与排行榜接口 channelId 同源）</summary>
    private void ChannelCard_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Button { Tag: int channelId } && channelId > 0)
        {
            Frame.Navigate(typeof(RankPage), channelId);
        }
    }
}
