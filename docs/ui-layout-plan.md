# AStation.Client UI 布局改造计划

> 审查时间：2026-10-28　审查范围：`MainWindow` + `Pages/*` + `Controls/*` + `App.xaml`
> 依据：源码逐文件阅读 + 多宽度实机采样（`tools/audit-layout.ps1`，产物在 `artifacts/audit/`）
> 状态：**待确认，未实施**

---

## 0. 审查结论

项目功能完整，播放链路（HLS 代理 / FFmpegInteropX / 弹幕 / 无边框全屏）实现质量高，**本次不动播放内核**。

布局层的问题是「每个页面各自为政」：

- 没有任何页面级宽度断点，只有 `MainWindow` 一个 850px 的搜索框断点；
- `AppMaxPageWidth=1920`、`AppNavViewMinWidth=240`、`AppTitleBarHeight=48`、`VideoGridPanel` 四个资源定义了但**从未被引用**；
- 卡片有 4 套互不相同的实现（16:9 动态 / 112×63 / 170×112 / 176×200）；
- 加载、空、失败三态在 5 个页面里有 4 种写法。

实机采样（880×660）显示：导航仍占 188px（21%），频道入口最后一列被窗口裁掉，卡片被拉到 314px 宽——这是本次改造要解决的核心问题。

更严重的是播放页：**800×640 时作者名逐字竖排**（`Grid` 的 `Auto / * / Auto` 把中列挤到近 0），**1000×720 时顶栏标题与集数重叠、控制栏「1.0x」压住「弹」按钮**，右侧 340 面板只占顶部 100px、下面 500px 全空。

采样截图（`artifacts/audit/`，已被 .gitignore 忽略）：

| 文件 | 尺寸 | 说明 |
|---|---|---|
| `home-1440x900.png` | 1440 | 5 列卡片，导航展开 |
| `home-1200x820.png` / `home-1040x740.png` | 1200 / 1040 | 4 / 3 列 |
| `home-880x660.png` | 880 | 仍 2 列，导航未收窄，频道入口右端被裁 |
| `narrow-排行榜.png` / `narrow-番剧.png` / `narrow-直播.png` | 800 | 三页在窄窗口下的实际表现 |
| `player-1280x820.png` / `player-1000x720.png` / `player-800x640.png` | 1280 / 1000 / 800 | 播放页三档：信息区塌陷、控制栏重叠、右栏大面积留白 |

---

## 1. 主窗口

**涉及文件**：`MainWindow.xaml` / `MainWindow.xaml.cs` / `App.xaml`

| # | 问题 | 证据 | 改造方法 |
|---|---|---|---|
| 1.1 | 导航永不收窄 | `PaneDisplayMode="Left"` 写死，`OpenPaneLength=188` | 按宽度切三态：≥1200 `Left(240)` / 1000–1200 `LeftCompact(48)` / <1000 `LeftMinimal`。设置里「常驻展开」作为上限开关，不覆盖断点 |
| 1.2 | 汉堡按钮与标题栏错行 | `AppTitleBar` 在 Row 0，NavigationView 在 Row 1，按钮 y≈60 | 关掉 `IsPaneToggleButtonVisible`，在标题栏左侧自绘汉堡/返回按钮（Bili.Copilot 做法），导航壳整体从 y=48 起且与标题栏左内边距对齐 |
| 1.3 | 标题栏信息层级弱 | 应用名用 `CaptionTextBlockStyle`(12px)，搜索框 `MinWidth=220/MaxWidth=520` 居中，右侧只有置顶+账号 | 结构改为 `[图标+名称 | 返回/前进 | 弹性搜索框 | 置顶/账号]`；<1100 隐藏应用名，<900 搜索框收成图标按钮 |
| 1.4 | 右侧让位靠改 Margin | `UpdateTitleBarInsets()` 每次 `Activated`/`Changed` 重算 `TitleBarTail.Margin` | 标题栏加第 4 列 `Width = TitleBar.RightInset` 的占位，激活时只更新这一列，避免最大化/还原抖动 |
| 1.5 | 导航与标题栏重复入口 | 导航有「登录」项，标题栏有账号按钮 | 移除导航「登录」，底部改为账号区（未登录=登录按钮，已登录=头像+昵称），与标题栏账号按钮共享同一状态 |
| 1.6 | 覆盖层没有统一壳 | `OpenOverlay` 只做 `MainFrame.Opacity=0.4` + `NavView.IsEnabled=false` | 抽 `OverlayHost`：scrim（`#66000000`）+ 卡片容器（MaxWidth 760、圆角 8、`ThemeShadow`）+ Esc/点 scrim 关闭，搜索与登录共用 |
| 1.7 | 背景双层 | `AppShellAcrylicBrush` 只在 Video/LiveRoom 用，盖在 Mica 上；其余页面透明 | 全站透明走 Mica；播放页用主题色 `#000`/透明，删除 Acrylic 层（全屏时也免去黑刷子硬编码） |

---

## 2. 播放页（点播 + 直播）

**涉及文件**：`Pages/VideoPage.xaml(.cs)` / `Pages/LiveRoomPage.xaml(.cs)` / `Controls/PlayerOverlay.xaml(.cs)`

| # | 问题 | 证据 | 改造方法 |
|---|---|---|---|
| 2.1 | 两栏无断点 | `SideColumn` 固定 340，只在全屏归 0；1000 宽时左列仅 414（播放器 414×233） | 三档：≥1280 侧栏 380 / 1040–1280 侧栏 340 / <1040 侧栏折到播放器下方（上下堆叠），用 `VisualState` 切换 `ColumnDefinitions` |
| 2.2 | 滚动职责混乱 | 页面 `RootScroll` 包全页，右栏列表又各自 `MaxHeight=560` | 页面不滚，左右两列各自滚（`Grid` 两列 `Height=*`）；左列信息区单独滚动。全屏时两列都撤 |
| 2.3 | 全屏状态散落 | `Overlay_FullscreenChanged` 里手改 5 个属性 + 背景刷子保存/恢复 | 收进 `PlayerPageStates`（Normal/Compact/Stacked/Fullscreen 四态）由 `VisualState` 驱动 |
| 2.4 | 右栏标题跳动 | `ListTitle` 随 tab 变成「分P 16」/「评论」 | 标题固定「选集 / 评论 / 相关」，计数单独放右侧；三 tab 改成分段控件（`RadioButtons` 样式或自绘 indicator），选中态用强调色下划线 + 文字加粗 |
| 2.5 | 假按钮 | 「点赞 / 收藏」是两个 `Border`，不可点 | 改 `Button`（`SubtleFillColorSecondary` 底、圆角 16），保留计数 |
| 2.6 | 简介展开跳变 | `Expander` 包在 `Border` 里，展开时整页高度跳 | 改「2 行截断 + 展开」，高度变化限制在左列内部，`MaxHeight` 带过渡 |
| 2.7 | 直播信息重复 | 分区/在线人数/流名在左列和右栏「直播信息」各出现一次 | 左列只留标题 + 主播 + 状态徽标 + 分区；解码/流信息全部收到右栏 |
| 2.8 | 直播弹幕是空壳 | 「弹幕」tab 只有一段说明文字 | 用统一空状态组件（见 §5），或暂时隐藏该 tab，通道接入后再开 |
| 2.9 | 16:9 写死 | `UpdatePlayerHeight` 按 `width / 1.778` 反算 | 暴露 `AspectRatio` 属性，直播竖屏用 9:16 + 信箱黑边；窗口模式仍限最大高度（不超过可用高度的 72%） |
| 2.10 | 控制层断点偏少 | 只有 1000（藏信息位）/ 880（藏音量） | 补 1280：宽屏显示完整信息位 + 集数；<760 再藏「后退/前进 10s」，只留播放/进度/音量/全屏 |
| 2.11 | **窄宽信息区塌陷** | 800 宽实测：作者名「neovega」逐字竖排成一行一个字，元信息同样竖排；播放器只剩 195×110 | 信息区 `Auto / * / Auto` 的 `*` 被两侧 Auto 挤到近 0。改 `*` 列加 `MinWidth`，右侧「点赞/收藏」在中列宽度 <320 时换行到下一行（`VisualState`） |
| 2.12 | **控制栏窄宽互相覆盖** | 1000 宽实测：顶栏标题与「第 1 / 共 1 集」重叠，控制栏「1.0x」与「弹」按钮叠在一起 | 顶栏改 `1* / Auto / 1*` 三列，标题 `TextTrimming` 且 `MaxWidth` 绑定列宽；控制层内按钮组用 `WrapPanel` 或按断点隐藏次要项（先藏「上一集/下一集」，再藏「1.0x」文字只留图标） |

---

## 3. 内容页面

**涉及文件**：`Pages/HomePage` / `ChannelPage` / `RankPage` / `BangumiPage` / `LivePage` / `Controls/VideoCardControl`

| # | 问题 | 证据 | 改造方法 |
|---|---|---|---|
| 3.1 | 无内容最大宽 | `AppMaxPageWidth=1920` 定义了没用；超宽屏卡片被拉得很大、列数过多 | 抽 `ContentHost`（透明 `Grid`，`MaxWidth` 断点 1600/1920，居中），所有内容页共用 |
| 3.2 | 首页层级扁平 | 「首页」28 / 「频道导航」20 / 「推荐视频」20 三段同级，无分组、无动作 | 结构：`Hero（正在热播 1 大卡 + 3 小卡）` → `分区入口（图标+名称，可横向滚）` → `推荐视频（标题 + 换一批 + 网格）` |
| 3.3 | 频道入口会溢出 | `ScrollViewer > ItemsWrapGrid MaxHeight=72`，按钮 `Width=100/Height=56`；880 宽时最后一列被裁且无滚动条 | 改 `ItemsRepeater + StackLayout(Horizontal)`，`HorizontalScrollBarVisibility=Auto`，两端加渐隐遮罩；按钮改 `MinWidth=96` 自适应 |
| 3.4 | 卡片四套实现 | 首页 16:9 动态；排行榜 112×63；直播 170×112；番剧 176×200（≈0.88，番剧封面是 3:4 会被裁） | 抽 `MediaCard` 基座（圆角/悬停/角标/标题 2 行/元信息 1 行）+ 三个规格：`VideoCard 16:9`、`BangumiCard 3:4`、`LiveCard 16:9 + LIVE 角标` |
| 3.5 | 无悬停态 | 卡片只有 `Tapped`，没有 `PointerOver` 视觉 | 统一悬停：`SubtleFillColorSecondary` 底 + 1px `CardStrokeColorDefault` 描边（不做缩放，避免虚拟化测量抖动） |
| 3.6 | 番剧右留白 | `ItemsWrapGrid ItemWidth=176` 固定，1440 下右侧空 68px | 改 `ItemsRepeater + UniformGridLayout MinItemWidth=160 ItemsStretch=Fill`，封面用 `Grid` 固定 3:4 |
| 3.7 | 排行榜行高不定 | 36 / 112 / `*` 三列，标题 2 行；`{Binding}` 非编译绑定 | 行高统一 76，标题 2 行 + 元信息 1 行；模板改 `x:DataType` + `x:Bind` |
| 3.8 | 直播卡片无卡片容器 | 封面只有上两角圆角，标题悬空 | 套 `MediaCard`，封面 16:9、标题 2 行、主播 1 行 |
| 3.9 | 频道页信息密度低 | 150×88 纯文字按钮 | 图标 + 名称 + 视频数，`MinItemWidth=160`，卡片式 |

---

## 4. 其他页面

| # | 问题 | 证据 | 改造方法 |
|---|---|---|---|
| 4.1 | 登录页窄窗口被裁 | `MaxWidth=900` + 固定列 `300 / 1 / 380` + `Padding 40` + `ColumnSpacing 32`，最小需 825px；1000 窗口内容区只有 812 | 列改 `1*`；<900 切单列（扫码在上、表单在下），去掉竖分隔线 |
| 4.2 | 搜索页是骨架 | `MinWidth=400`、结果只有标题+作者、无三态 | 结果卡片化（封面 + 标题 + 作者 + 播放量）+ 分段（视频/用户/番剧）+ `StatePanel` |
| 4.3 | 用户空间是占位 | 「用户页面开发中……」 | 只做骨架：封面 + 头像 + 统计 + 作品网格；数据保留空态 |
| 4.4 | 设置页贴左 | `MaxWidth=920` + `HorizontalAlignment=Left` | 卡片 `Stretch` + `MaxWidth=920` + 居中；行内控件 <720 时换行到下一行，Slider 固定 200 改弹性 |
| 4.5 | 关于页是模板 | 英文「This is the About page」 | 版本 + 技术栈 + 开源许可 + 链接（最低优先级） |

---

## 5. 状态与视觉规范

| # | 问题 | 改造方法 |
|---|---|---|
| 5.1 | 三态四套写法 | 首页 `ProgressBar`（隐藏/显示会推动内容）、排行榜 `HintText + 14px Ring`、番剧居中 `EmptyPanel`、直播/频道只有提示文字 → 抽 `StatePanel`：`Loaded / Loading / Empty / Error` 四态，图标 20–24、标题 16、描述 13、主按钮，**叠在内容区 Grid 上**不占布局，消除跳动 |
| 5.2 | 加载态无骨架 | 列表页加骨架（3 行卡片占位，`Opacity 0.4` 呼吸），替代顶部 ProgressBar |
| 5.3 | 尺寸硬编码 | 页面 `Padding` 有 `24,16` / `24,8` / `20,4` 三种；颜色 `#99000000`、`#33F0483E`、`#FFF0483E` 散落 → 建 `Styles/Tokens.xaml`（间距/圆角/字号/颜色）+ `Styles/Controls.xaml`，`App.xaml` 只合并 |
| 5.4 | 主题跟随 | 全屏黑底是 `new SolidColorBrush(Colors.Black)`，切主题不重算 → 改 `ThemeResource` |

**规范基线**

- 断点：`<900` 紧凑（导航 Minimal、单列、播放页上下堆叠）/ `900–1200` 中（导航 Compact、2–3 列）/ `1200–1600` 宽（导航展开 240、4–5 列、侧栏 340）/ `≥1600` 超宽（内容 1600 居中、侧栏 380）
- 间距：页面 24（<900 → 16）、区块 24、卡片 16、卡内 12
- 字号：页面标题 28 SemiBold / 区块 20 SemiBold / 卡片标题 14（2 行）/ 元信息 12 次级 / 徽标 11
- 圆角：卡片 8、按钮 4、芯片 16、播放器 10（全屏 0）
- 卡片：视频 16:9、番剧 3:4、直播 16:9 + LIVE、排行榜行 112×63

---

## 6. 分阶段实施

| 阶段 | 内容 | 涉及文件 | 验收 |
|---|---|---|---|
| **P0 基础** | `Tokens.xaml` + `Controls.xaml` + 主题字典重建，把现有硬编码尺寸迁入资源（视觉不应变化） | `App.xaml`、新增 `Styles/*`、全部页面（只替换资源引用） | 构建 + 五档宽度截图与改造前逐像素对比 |
| **P1 主窗口** | 标题栏重构、导航三态、搜索框弹性、`OverlayHost`、`ContentHost` | `MainWindow.xaml(.cs)`、`App.xaml` | 900/1040/1280/1600/2560 五档截图；导航展开/收起、覆盖层 Esc |
| **P2 播放页** | 四态 VisualState、右栏分段控件、滚动职责、直播信息去重、点赞收藏改按钮、**修信息区塌陷与控制栏重叠** | `VideoPage`、`LiveRoomPage`、`PlayerOverlay` | 点播/直播 × 800/1000/1280/1600 + 全屏；800 宽不得出现竖排文字与按钮重叠 |
| **P3 内容页** | 首页改版、`MediaCard` 三规格、番剧/直播/频道/排行榜迁移 | `HomePage`、`BangumiPage`、`LivePage`、`ChannelPage`、`RankPage`、`VideoCardControl` | 各页四档宽度 + 悬停 + 空态 |
| **P4 其他 + 状态** | 登录响应式、搜索页、用户空间骨架、设置页、`StatePanel` 全站替换 | `LoginPage`、`Overlay/*`、`SettingsPage`、`AboutPage` | 三态截图 + 深浅色 + 键盘 |

每个阶段独立提交，独立构建 + 单测 + 截图验证；不通过不进入下一阶段。

---

## 7. 需要你确认的 5 个决策

1. **评论放哪**：留在 340 右栏，还是移到播放器下方通栏（B 站/Bili.Copilot 主流做法，更适合长评论）？—— 影响最大，建议移出。
2. **播放页窄窗口（<1040）**：右栏折叠成覆盖抽屉，还是改成播放器下方堆叠？—— 建议堆叠。
3. **首页「频道导航」**：保留 12 个平铺入口，还是改成 6 个常用分区 + 「全部分区」？
4. **首页是否上 Hero 区**（大封面 + 标题 + 播放按钮）：现有排行榜接口的封面足够，不需要新接口。
5. **视觉边界**：是否严格只用 Fluent 原生控件与系统资源（不引入自绘卡片悬停、分段控件这类轻定制）？

## 8. 风险

- `NavigationView` 切 `PaneDisplayMode` 会与 `SetChromeVisible` 保存/恢复的快照冲突（全屏进出时可能恢复成旧的 `Left`）→ 改模式时同步更新快照。
- `UniformGridLayout + ItemsStretch=Fill` 在断点切换时需重新测量，可能出现一帧抖动 → 用 `VisualState` 改 `MinItemWidth` 而不是改 `ItemWidth`。
- 抽 `MediaCard` 会同时改动 5 个页面的模板与点击事件 → 每页单独验证「点卡片进详情/进直播间」。

## 9. 本次不改

HLS 本地代理、FFmpegInteropX 解码、MediaPlayerElement、弹幕逐帧推进、无边框全屏 Win32、登录/评论/取流等业务逻辑与数据模型。
