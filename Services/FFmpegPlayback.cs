using System;
using System.Threading.Tasks;
using FFmpegInteropX;
using Windows.Media.Playback;

namespace AStation_Client.Services;

/// <summary>
/// FFmpeg 解码桥接。
/// · 直播：AcFun 只提供 FLV（HLS 返回 129001），Media Foundation 不支持 FLV 容器，必须走 FFmpeg
/// · 点播：HLS 偶尔会被系统解码器拒绝（"视频类型不受支持"），这里作为兜底
/// 解封装 + 解码后把 MediaPlaybackItem 交给 MediaPlayerElement。
/// </summary>
public static class FFmpegPlayback
{
    /// <summary>
    /// 为 FLV 地址创建可播放源。
    /// </summary>
    /// <param name="url">FLV / HLS 地址（带时效签名）</param>
    /// <param name="hardwareDecode">true 走 D3D11 硬解，false 走 FFmpeg 软解</param>
    public static async Task<IMediaPlaybackSource> CreateAsync(string url, bool hardwareDecode = true)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("拉流地址为空", nameof(url));

        var config = new MediaSourceConfig();

        // 默认就是 D3D11 硬解；关掉时显式要求 FFmpeg 软解，兼容花屏 / 黑屏的机器
        if (!hardwareDecode)
        {
            config.Video.VideoDecoderMode = VideoDecoderMode.ForceFFmpegSoftwareDecoder;
        }

        var source = await FFmpegMediaSource.CreateFromUriAsync(url, config);
        return source.CreateMediaPlaybackItem();
    }
}
