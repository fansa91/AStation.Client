# AStation.Client.Console

AcFun 接口命令行工具。只引用 `AStation.Client.Core`，不带任何 WinUI 依赖——
界面里用的服务层（搜索、排行、详情、评论、弹幕、取流、直播、番剧、表情字典）原样复用，
所以它既是调试工具，也是这套 API 的最小使用示例。

## 构建与运行

```bash
dotnet build AStation.Client.Console/AStation.Client.Console.csproj -c Debug

# 产物
AStation.Client.Console/bin/Debug/net10.0/AStation.Client.Console.exe help
```

> 本机 .NET 装在非默认路径时，运行前要 `$env:DOTNET_ROOT='C:\dotnet'`。

命名空间用的是 `AStation_Client.Cli` 而不是 `AStation.Client.Console`：
后者会让代码里的 `Console.Write` 解析成命名空间，全项目编译不过。
程序集名仍然是 `AStation.Client.Console`。

## 命令

| 命令 | 说明 |
| --- | --- |
| `search <关键词>` | 搜索视频。`--sort 1\|2\|5`（综合 / 最多播放 / 最新发布），`--limit` 最大 30 |
| `rank` | 排行榜。`--channel <一级频道 id>`，`--period today\|3d\|week` |
| `video <acId>` | 视频详情：UP 主、频道、播放互动数据、分P 列表（含每个分P 的 videoId） |
| `comments <acId>` | 评论 + 楼中楼。`--page`、`--type`（3 视频 / 2 番剧）、`--raw` 看未解析正文 |
| `danmaku <videoId>` | 弹幕。注意用**分P 的 videoId**（`video` 命令最后一列），不是 acId |
| `bangumi list` / `bangumi <id>` | 番剧库 / 番剧详情与剧集 |
| `live list` | 直播间列表。`--channel 0\|1\|2\|3\|4`（全部 / 游戏 / 其他 / 娱乐 / 虚拟偶像） |
| `live play <authorId> <liveId>` | 直播拉流档位（FLV 地址） |
| `play <resourceId>` | 点播取流：清晰度、码率、CDN 线路、m3u8 地址 |
| `download <resourceId>` | 下载点播视频，拼成单个 `.ts`；`--mp4` 用 ffmpeg 无损转封装 |
| `download --url <m3u8>` | 直接下任意 HLS 清单 |
| `emotes` | 表情字典：包列表、`--keyword` 过滤、`--limit` |
| `ubb "<文本>"` | 看 UBB 正文被解析成什么（表情 / @ / 图片 / 换行） |

通用开关：`--json`（原始结构）、`--h264`（不要 H.265 清单）、`--raw`（原始正文）。

## 几个实现要点

- **分片签名**：AcFun 的 m3u8 里分片是相对路径，`sign/t/us` 却挂在清单的 query 上。
  按 RFC 解析相对路径会丢掉 query → 分片 403。`Services/HlsDownloader` 解析后把清单的
  query 手动补回分片地址；界面里对应的是 `Services/LocalMediaProxy`（本地代理重写清单）。
- **AES-128**：清单带 `EXT-X-KEY` 时按 `EXT-X-IV`（缺省用分片序号，16 字节大端）解密。
- **表格对齐**：中文按两格宽计算，`Cli/Output` 里自己做显示宽度与截断，`string.Length`
  补空格会让每列都歪。
- **退出码**：0 成功、1 接口/网络失败、2 参数用法错误、130 手动取消（Ctrl+C）。

## 和 Core 的关系

每个命令只是「拼参数 → 调 ServiceLocator 里的服务 → 打印」。想加新命令，
在 `Commands/` 里加一个静态方法，再到 `Program.cs` 的 switch 里挂上即可；
需要新的接口能力，应该加在 `AStation.Client.Core/Services` 里，界面和命令行同时受益。
