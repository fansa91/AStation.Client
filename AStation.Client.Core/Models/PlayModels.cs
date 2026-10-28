namespace AStation_Client.Services;

/// <summary>
/// 一档清晰度（点播）——来自 ksPlayJson.adaptationSet[0].representation[]
/// </summary>
public class PlayQuality
{
    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? BackupUrl { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Bitrate { get; set; }
    public bool IsHevc { get; set; }

    /// <summary>可选 CDN 线路（主源 + backupUrl[] 全部），按接口下发顺序</summary>
    public List<CdnSource> CdnSources { get; } = new();

    /// <summary>分辨率说明，如 "1920×1080"</summary>
    public string ResolutionText => Width > 0 && Height > 0 ? $"{Width}×{Height}" : string.Empty;

    public override string ToString() => Label;
}

/// <summary>
/// 一条 CDN 线路。AcFun 同一档清晰度会下发多个源：
/// 主源通常是腾讯云（tx-video.acfun.cn），backupUrl[] 里是阿里云（ali-video.acfun.cn）等。
/// </summary>
public class CdnSource
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    /// <summary>是否来自 backupUrl（主源为 false）</summary>
    public bool IsBackup { get; set; }

    /// <summary>线路主机名，用于界面上区分同名线路</summary>
    public string Host { get; set; } = string.Empty;

    public override string ToString() => Name;
}

/// <summary>
/// 点播取流结果：可选清晰度列表 + 续播进度
/// </summary>
public class PlayStreamResult
{
    public List<PlayQuality> Qualities { get; } = new();

    /// <summary>Streams 为空时说明取流失败，这里带原因</summary>
    public string? ErrorMessage { get; set; }

    public bool IsHevc { get; set; }
    public double DurationSeconds { get; set; }

    /// <summary>服务端下发的续播进度（登录态才有）</summary>
    public long UserPlayedSeconds { get; set; }

    public bool HasStream => Qualities.Count > 0;

    /// <summary>最高清晰度</summary>
    public PlayQuality? Best => Qualities.FirstOrDefault();
}

/// <summary>
/// 直播码率档位——来自 getPlayUrls 的 liveAdaptiveManifest[0].adaptationSet.representation[]
/// </summary>
public class LiveStreamQuality
{
    public string Name { get; set; } = string.Empty;
    public int Bitrate { get; set; }
    public string Url { get; set; } = string.Empty;

    public string BitrateText => Bitrate > 0 ? $"{Bitrate} kbps" : string.Empty;

    public override string ToString() => Name;
}

/// <summary>
/// 直播间拉流结果
/// </summary>
public class LiveStreamResult
{
    public List<LiveStreamQuality> Qualities { get; } = new();
    public string StreamName { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public bool HasStream => Qualities.Count > 0;

    /// <summary>默认取最高码率</summary>
    public LiveStreamQuality? Best => Qualities.LastOrDefault();
}

/// <summary>
/// 视频详情（来源：/v/ac{dougaId} 页面内嵌的 window.videoInfo）
/// </summary>
public class VideoDetail
{
    public string DougaId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CoverUrl { get; set; } = string.Empty;

    public string ChannelName { get; set; } = string.Empty;

    /// <summary>频道 id（二级），相关视频按它取同频道内容</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>父频道 id（一级）</summary>
    public string ParentChannelId { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;
    public string AuthorId { get; set; } = string.Empty;
    public string AuthorAvatar { get; set; } = string.Empty;
    public long AuthorFans { get; set; }

    public long ViewCount { get; set; }
    public long DanmakuCount { get; set; }
    public long CommentCount { get; set; }
    public long LikeCount { get; set; }
    public long StowCount { get; set; }
    public long BananaCount { get; set; }
    public long CreateTimeMillis { get; set; }

    /// <summary>当前分P的 videoId（弹幕按分P挂，这个才是弹幕的 resourceId）</summary>
    public string CurrentVideoId { get; set; } = string.Empty;

    /// <summary>分P 列表；单 P 视频只有一条</summary>
    public List<VideoPart> Parts { get; } = new();
}

/// <summary>
/// 视频分P
/// </summary>
public class VideoPart
{
    public string VideoId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public long DurationMillis { get; set; }
    public int DanmakuCount { get; set; }

    public string DurationText => DurationMillis <= 0
        ? string.Empty
        : TimeSpan.FromMilliseconds(DurationMillis).ToString(DurationMillis >= 3600_000 ? @"hh\:mm\:ss" : @"mm\:ss");

    public override string ToString() => Title;
}

/// <summary>
/// 番剧详情（来源：/bangumi/aa{bangumiId} 页面内嵌的 window.bangumiData + 集列表）
/// </summary>
public class BangumiDetail
{
    public string BangumiId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Introduction { get; set; } = string.Empty;
    public string Cover { get; set; } = string.Empty;

    /// <summary>当前集名称，如「第1集」</summary>
    public string EpisodeName { get; set; } = string.Empty;

    /// <summary>播放授权 key，切集时必须带上</summary>
    public string Mkey { get; set; } = string.Empty;

    /// <summary>当前集对应的 videoId（番剧弹幕用它）</summary>
    public string CurrentVideoId { get; set; } = string.Empty;

    public int ItemCount { get; set; }
    public long PlayCount { get; set; }
    public long StowCount { get; set; }
    public long LikeCount { get; set; }
    public bool NeedPay { get; set; }

    /// <summary>更新状态，如「已完结」「更新至第8话」</summary>
    public string UpdateStatus { get; set; } = string.Empty;

    public List<BangumiEpisode> Episodes { get; } = new();
}

/// <summary>
/// 番剧单集
/// </summary>
public class BangumiEpisode
{
    public string ItemId { get; set; } = string.Empty;
    public string VideoId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool NeedPay { get; set; }

    public override string ToString() => Name;
}
