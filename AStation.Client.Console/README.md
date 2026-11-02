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
| `livedanmaku <authorId>` | 直播间实时弹幕（快手 `zt.live` TCP 长连接）。`--seconds` 监听时长，`--json` 汇总输出 |
| `play <resourceId>` | 点播取流：清晰度、码率、CDN 线路、m3u8 地址 |
| `download <resourceId>` | 下载点播视频，拼成单个 `.ts`；`--mp4` 用 ffmpeg 无损转封装 |
| `download --url <m3u8>` | 直接下任意 HLS 清单 |
| `emotes` | 表情字典：包列表、`--keyword` 过滤、`--limit` |
| `ubb "<文本>"` | 看 UBB 正文被解析成什么（表情 / @ / 图片 / 换行） |
| `login` | 账号密码登录（见下节），成功后把 Cookie 存盘 |
| `whoami` | 用存下的 Cookie 探一次 `personalInfo`，看登录态 |
| `config [show\|path\|init]` | 看 / 建本地配置文件（见下节） |

通用开关：`--json`（原始结构）、`--h264`（不要 H.265 清单）、`--raw`（原始正文）。

## 登录

AcFun 的登录态是 **Cookie**，没有独立 token。命令行每次都是新进程，
所以 `login` 成功后会把 Cookie 落到 `%LOCALAPPDATA%\AStation.Client\cli-cookies.json`，
后续用 `whoami`（或 `--cookies <路径>`）复用。
桌面端登录过的话，`whoami` 会自动回退去读它那份 `cookies.json`，不必再登一遍。

```bash
# 交互式：自动取验证码、存成 PNG、提示你输入图中的字符
AStation.Client.Console.exe login --username 13800000000 --password ***

# 脚本式：自己取图看字符，再指定 key + code
AStation.Client.Console.exe login --username 手机号 --password 密码 --captcha-key <key> --captcha-code KJ9i

AStation.Client.Console.exe whoami
```

- 账号必须是**手机号或邮箱**，老式用户名会被服务端拒绝（`result=100001017`）
- 图形验证码**区分大小写**（`KJ9i` 通过、`kj9i` 报 `100001016`），一张图只能对上一组字符
- 短期内多次登录才会强制验证码；正常情况服务端也会给图，按提示填即可
- 扫码登录（`qr/start` → `scanResult`）在桌面端走不通：签名与客户端指纹绑定，
  `scanResult` 恒返回 `result=10 client error`，所以只做了账号密码这条线

## 配置文件

格式照 BBDown 的 `BBDown.config`：**一行一个参数**，需要值的选项把值写在**下一行**，
`#` 开头是注释，空行忽略。因为一行就是一个完整参数，值里带空格、带引号都不用转义。

```config
# AStation.Client.Console.config
# 默认不要 H.265 清单
--h264

# 列表默认 10 条
--limit
10

# 下载默认 720P 并转 mp4
--quality
720
--mp4
```

放置位置与优先级：

1. `--config-file <路径>` 指定的文件（缺文件直接报错）
2. 程序目录（exe 旁边）的 `AStation.Client.Console.config`
3. 当前工作目录的同名文件

配置里的参数排在命令行参数**之前**，所以命令行上再写一次就覆盖它
（`search 猫 --limit 3` 会盖掉配置里的 10）。`--no-config` 临时忽略配置文件。
配置里不要写命令名和裸参数——只放选项，写错了会在 stderr 提示。

相关命令：

```bash
AStation.Client.Console.exe config          # 看生效的配置文件与解析出的参数
AStation.Client.Console.exe config path     # 只输出路径，脚本里用（没找到退出码 1）
AStation.Client.Console.exe config init     # 当前目录写一份全注释模板
AStation.Client.Console.exe config init --path C:\Tools\ac.config --force
```

诊断信息（加载了哪个配置、哪行写错）走 **stderr**，不会污染 `--json` 的 stdout。

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
