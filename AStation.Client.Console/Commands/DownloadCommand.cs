using System.Diagnostics;
using AStation_Client.Services;
using AStation_Client.Cli.Services;

namespace AStation_Client.Cli.Commands;

/// <summary>
/// <c>download</c>：按清晰度下载点播视频，拼成单个 .ts（可选再转 mp4）。
///
/// 例：
///   download 12345678 --quality 1080P -o 12345678.ts
///   download 12345678 --type 1 --mkey xxxx --quality 720
///   download --url "https://.../index.m3u8" -o clip.ts
/// </summary>
public static class DownloadCommand
{
    public static async Task<int> RunAsync(CliArgs args)
    {
        string? quality = args.Get("quality", "q");
        string? explicitOut = args.Get("out", "o");
        bool toMp4 = args.Has("mp4");

        string sourceUrl;
        string label;

        if (args.Get("url", "u") is { } direct)
        {
            sourceUrl = direct;
            label = "url";
        }
        else
        {
            string resourceId = args.Require(0, "resourceId（视频填 acId，番剧填 bangumiId）");
            int resourceType = args.GetInt("type", 2);
            string? videoId = args.Get("video");
            string? mkey = args.Get("mkey");

            var response = await ServiceLocator.Play.GetPlayInfoAsync(
                resourceId, resourceType, videoId, mkey, CliContext.Token);

            bool preferHevc = !args.Has("h264") && AppSettings.Current.PreferHevc;
            var play = ServiceLocator.Play.BuildPlayResult(response, preferHevc);

            if (!play.HasStream)
            {
                Out.Error(play.ErrorMessage ?? "没有取到播放地址");
                return 1;
            }

            var picked = Format.PickQuality(play.Qualities, quality)
                         ?? throw new CliUsageException("没有可用的清晰度");

            sourceUrl = picked.Url;
            label = picked.Label;
            Out.Info($"  清晰度 {picked.Label}（{picked.ResolutionText}）· 时长 {Out.Duration(play.DurationSeconds)}");
        }

        string outPath = explicitOut ?? $"ac-video-{label.Replace('+', 'p')}.ts";
        if (toMp4) outPath = Path.ChangeExtension(outPath, ".mp4");

        string tempPath = toMp4 ? Path.ChangeExtension(outPath, ".ts") : outPath;

        Out.Dim($"  清单 {HlsDownloader.Shorten(sourceUrl)}");
        Out.Info($"  输出 {Path.GetFullPath(tempPath)}");

        var downloader = new HlsDownloader();

        // 输出被重定向时不要刷 \r 进度条，否则日志里全是重复行
        IProgress<double>? progress = Out.SupportsProgress ? new Progress<double>(RenderProgress) : null;

        var result = await downloader.DownloadAsync(sourceUrl, tempPath, progress, CliContext.Token);
        if (progress is not null)
        {
            Console.Write('\r');
            Console.Write(new string(' ', 64));
            Console.Write('\r');
        }

        Out.Ok($"  完成：{result.SegmentCount} 个分片 · {result.Bytes / 1024.0 / 1024.0:F1} MB");
        Out.Line($"  {result.OutputPath}");

        if (!toMp4)
        {
            Out.Dim("  提示：.ts 可以直接用播放器打开；要 mp4 加 --mp4（需要 ffmpeg 在 PATH 里）");
            return 0;
        }

        return TryRemux(tempPath, outPath);
    }

    private static void RenderProgress(double value)
    {
        int filled = (int)Math.Round(value * 28);
        string bar = new string('#', filled) + new string('-', 28 - filled);
        Console.Write($"\r  下载 [{bar}] {value * 100,5:F1}%");
    }

    /// <summary>用 ffmpeg 无损转封装；没有 ffmpeg 就保留 ts</summary>
    private static int TryRemux(string tsPath, string mp4Path)
    {
        string? ffmpeg = FindOnPath("ffmpeg.exe") ?? FindOnPath("ffmpeg");
        if (ffmpeg is null)
        {
            Out.Warn("  没找到 ffmpeg，跳过转 mp4（ts 文件仍然可用）");
            Out.Line($"  {Path.GetFullPath(tsPath)}");
            return 0;
        }

        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        info.ArgumentList.Add("-y");
        info.ArgumentList.Add("-loglevel");
        info.ArgumentList.Add("error");
        info.ArgumentList.Add("-i");
        info.ArgumentList.Add(tsPath);
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("copy");
        info.ArgumentList.Add(mp4Path);

        try
        {
            using var process = Process.Start(info);
            if (process is null) throw new InvalidOperationException("ffmpeg 启动失败");
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                Out.Warn("  ffmpeg 转封装失败，保留 ts");
                Out.Line($"  {Path.GetFullPath(tsPath)}");
                return 0;
            }

            File.Delete(tsPath);
            Out.Ok($"  已转封装：{Path.GetFullPath(mp4Path)}");
            return 0;
        }
        catch (Exception ex)
        {
            Out.Warn($"  转封装出错（{ex.Message}），保留 ts");
            Out.Line($"  {Path.GetFullPath(tsPath)}");
            return 0;
        }
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                string candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // PATH 里有非法路径是常态，跳过
            }
        }

        return null;
    }
}
