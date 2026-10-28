using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AStation_Client.Services;

/// <summary>
/// 一条评论（主楼）。字段取自 GET /rest/pc-direct/comment/list 的 rootComments[]。
/// 注意接口字段嵌套很深（头像在 headUrl[].url 或 userHeadImgInfo.thumbnailImage.cdnUrls[].url），
/// 这里统一在 Service 里解析好再交出来。
///
/// 楼中楼：接口在同一个响应里用 subCommentsMap["&lt;主楼 commentId&gt;"] = { pcursor, subComments[] }
/// 带回（每个主楼固定只给前 3 条，暂无单独的「加载更多回复」接口），
/// 所以回复随主楼一起解析，界面上由 IsExpanded 控制展开 / 收起。
/// </summary>
public class CommentItem : INotifyPropertyChanged
{
    public long CommentId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Avatar { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public int LikeCount { get; set; }
    public string LikeCountText => LikeCount > 0 ? LikeCount.ToString() : string.Empty;

    /// <summary>接口直接给的相对时间，如「4分钟前」</summary>
    public string PostDate { get; set; } = string.Empty;

    public long TimestampMillis { get; set; }

    /// <summary>楼中楼条数</summary>
    public int SubCommentCount { get; set; }
    public string SubCommentText => SubCommentCount > 0 ? $"{SubCommentCount} 条回复" : string.Empty;

    /// <summary>是否 UP 主本人</summary>
    public bool IsUp { get; set; }

    /// <summary>是否置顶</summary>
    public bool IsSticky { get; set; }

    public int Floor { get; set; }

    /// <summary>回复某人时的被回复者昵称（楼中楼才有）</summary>
    public string ReplyToUserName { get; set; } = string.Empty;

    /// <summary>头像是否有图（没图时界面用首字母占位）</summary>
    public bool HasAvatar => !string.IsNullOrWhiteSpace(Avatar);

    /// <summary>自己是不是楼中楼里的一条回复</summary>
    public bool IsReply { get; set; }

    /// <summary>楼中楼回复（接口每个主楼最多下发 3 条）</summary>
    public List<CommentItem> Replies { get; } = new();

    private bool _isExpanded;

    /// <summary>楼中楼是否展开（绑到界面，改它即可展开 / 收起）</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            Raise();
            Raise(nameof(ReplyToggleText));
            Raise(nameof(ReplyToggleGlyph));
        }
    }

    /// <summary>有回复内容时才给「展开」入口</summary>
    public bool CanExpand => Replies.Count > 0;

    /// <summary>回复数大于已下发条数时，用纯文本提示（没有可展开的内容）</summary>
    public bool ShowPlainSubText => SubCommentCount > 0 && Replies.Count == 0;

    public string ReplyToggleText => _isExpanded
        ? "收起回复"
        : $"展开 {Replies.Count} 条回复";

    /// <summary>展开 / 收起的小箭头</summary>
    public string ReplyToggleGlyph => _isExpanded ? "\uE70E" : "\uE70D";

    /// <summary>「回复 @某某」前缀，非楼中楼或没指名时为空</summary>
    public string ReplyToText => IsReply && !string.IsNullOrWhiteSpace(ReplyToUserName)
        ? $"回复 @{ReplyToUserName}"
        : string.Empty;

    public bool HasReplyTo => !string.IsNullOrWhiteSpace(ReplyToText);

    /// <summary>接口只给前 3 条，剩下的条数提示；没有差距时为空</summary>
    public string HiddenReplyText => SubCommentCount > Replies.Count
        ? $"另有 {SubCommentCount - Replies.Count} 条回复未下发"
        : string.Empty;

    public bool HasHiddenReplies => SubCommentCount > Replies.Count;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 评论列表结果
/// </summary>
public class CommentResult
{
    public List<CommentItem> Items { get; } = new();
    public long TotalCount { get; set; }
    public int Page { get; set; }
    public int TotalPage { get; set; }

    /// <summary>失败原因（result != 0 或网络异常）</summary>
    public string? ErrorMessage { get; set; }

    public bool HasMore => Page < TotalPage;
}

/// <summary>
/// 相关视频条目。来源：POST /rest/pc-direct/channel/resource/query/match 的 feed[]。
/// （文档里没有独立的「相关推荐」接口，同频道内容流是最接近的可用数据源）
/// </summary>
public class RelatedVideoItem
{
    public string DougaId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Cover { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;

    public long DurationMillis { get; set; }
    public long ViewCount { get; set; }
    public long DanmakuCount { get; set; }

    /// <summary>接口给的相对时间，如「5小时前」</summary>
    public string CreateTime { get; set; } = string.Empty;

    public string DurationText => DurationMillis <= 0
        ? string.Empty
        : TimeSpan.FromMilliseconds(DurationMillis).ToString(DurationMillis >= 3600_000 ? @"hh\:mm\:ss" : @"mm\:ss");

    /// <summary>列表副标题：UP主 · 时长 · 播放</summary>
    public string SubText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Author)) parts.Add(Author);
            if (!string.IsNullOrWhiteSpace(DurationText)) parts.Add(DurationText);
            parts.Add($"{FormatCount(ViewCount)} 播放");
            return string.Join(" · ", parts);
        }
    }

    internal static string FormatCount(long value) => value switch
    {
        >= 100_000_000 => $"{value / 100_000_000.0:0.#}亿",
        >= 10_000 => $"{value / 10_000.0:0.#}万",
        _ => value.ToString()
    };
}
