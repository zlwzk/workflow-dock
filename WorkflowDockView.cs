using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace WorkflowDock;

/// <summary>
/// 岛上的视图：同一棵可视树同时承载「小岛（紧凑态）」和「大岛（展开态）」，
/// 宿主切换形态时调用 AnimateToExpanded / AnimateToCompact 让内部元素同步变形。
///
/// 五条要点（与技能铁律一致）：
///   1. 所有元素（含只在展开态出现的）一开始就常驻可视树；
///   2. 隐藏用 Height = 0 + Opacity = 0，不要用 Visibility = Collapsed（无法过渡）；
///   3. 形态动画逐帧直接赋值，**不要用 Storyboard**（动态加载的插件程序集里
///      属性路径动画会在 tick 上抛 COMException 0x800F1001，导致大岛收起后卡死）；
///   4. 缓动与宿主保持一致：BackEase(EaseOut, Amplitude: 0.45)；
///   5. 配色跟着岛体主题走：中性色用共享画刷，主题一变只改画刷的 Color。
/// </summary>
public sealed class WorkflowDockView : UserControl, IMorphView
{
    private const double BackAmplitude = 0.45;   // 与宿主内置视图同一条曲线，手感一致

    private const double CompactTile = 26;       // 紧凑态图标方块边长
    private const double ExpandedTile = 36;      // 展开态图标方块边长
    private const double CompactGlyph = 12;      // 紧凑态首字字号
    private const double ExpandedGlyph = 16;     // 展开态首字字号
    private const double CommandHeight = 34;     // 展开态命令框高度
    private const int MaxApps = 6;               // 小岛宽度有限，最多显示前 6 个

    private readonly IIslandTheme _theme;
    private readonly Action<AppEntry> _launch;
    private readonly Func<string, string> _runCommand;

    private readonly StackPanel _appsPanel;
    private readonly TextBox _commandBox;

    private readonly List<Border> _tiles = new();
    private readonly List<TextBlock> _tileGlyphs = new();

    /// <summary>中性色一律用共享画刷：主题一变只改 Color，所有用到的地方当场跟着变。</summary>
    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _tileBrush = new();
    private readonly SolidColorBrush _tileHoverBrush = new();

    private readonly DispatcherQueueTimer? _morphTimer;
    private DateTimeOffset _morphStart;
    private TimeSpan _morphDuration = TimeSpan.FromMilliseconds(333);
    private double _morphFrom;
    private double _morphTarget;

    /// <summary>当前形态进度：0 = 紧凑态，1 = 展开态。反转动画时从这里接着走。</summary>
    private double _progress;

    public WorkflowDockView(
        PluginManifest manifest,
        IIslandTheme theme,
        Action<AppEntry> launch,
        Func<string, string> runCommand)
    {
        _theme = theme;
        _launch = launch;
        _runCommand = runCommand;

        ApplyThemeColors();
        _theme.Changed += ApplyThemeColors;   // 岛体换主题（Fluent 跟随系统明暗；Apple 恒为深色）

        _appsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // 展开态才显示的命令框：Height/Opacity 归零藏起来（元素仍在可视树里）。
        // 标准 TextBox 在宿主 ≥ 2.4.0 上可直接打字（含中文输入法），不要自己接键盘输入。
        _commandBox = new TextBox
        {
            PlaceholderText = "输入网址 / 路径 / 名称，回车执行",
            FontSize = 13,
            Height = 0,
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _commandBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                var text = _commandBox.Text ?? string.Empty;
                _commandBox.Text = string.Empty;
                if (text.Trim().Length > 0)
                {
                    _runCommand(text);   // 反馈由插件统一弹临时消息
                }
            }
        };

        var root = new StackPanel
        {
            Spacing = 8,
            Padding = new Thickness(14, 7, 14, 7),
            VerticalAlignment = VerticalAlignment.Center,
        };
        root.Children.Add(_appsPanel);
        root.Children.Add(_commandBox);

        Content = root;

        ReloadApps([]);

        // 逐帧动画用的 UI 线程定时器；拿不到就退化成「直接切终态」，绝不留下中间值
        _morphTimer = DispatcherQueue?.CreateTimer();
        if (_morphTimer is not null)
        {
            _morphTimer.Interval = TimeSpan.FromMilliseconds(16);
            _morphTimer.IsRepeating = true;
            _morphTimer.Tick += (_, _) => OnMorphTick();
        }

        Unloaded += (_, _) => _morphTimer?.Stop();
    }

    public UIElement View => this;

    /// <summary>按新的 App 列表重建图标（设置变更时调用）。</summary>
    public void ReloadApps(IReadOnlyList<AppEntry> apps)
    {
        _appsPanel.Children.Clear();
        _tiles.Clear();
        _tileGlyphs.Clear();

        foreach (var app in apps.Take(MaxApps))
        {
            var glyph = new TextBlock
            {
                Text = app.Label.Length > 0 ? char.ToUpperInvariant(app.Label[0]).ToString() : "?",
                FontSize = CompactGlyph,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = _textBrush,
            };

            var tile = new Border
            {
                Width = CompactTile,
                Height = CompactTile,
                CornerRadius = new CornerRadius(CompactTile / 2),
                Background = _tileBrush,
                Child = glyph,
            };

            var button = new Button
            {
                Content = tile,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
            };
            ToolTipService.SetToolTip(button, app.Label);
            button.Click += (_, _) =>
            {
                try
                {
                    _launch(app);
                }
                catch (Exception ex)
                {
                    // 启动回调里的异常自己兜住，别计入宿主未处理异常
                    System.Diagnostics.Debug.WriteLine($"launch failed: {ex.Message}");
                }
            };

            _appsPanel.Children.Add(button);
            _tiles.Add(tile);
            _tileGlyphs.Add(glyph);
        }

        // 重建后立刻按当前进度铺一次尺寸，避免新图标停留在默认大小
        ApplyMorph(_progress);
    }

    public void AnimateToExpanded(TimeSpan duration) => StartMorph(expanded: true, duration);

    public void AnimateToCompact(TimeSpan duration) => StartMorph(expanded: false, duration);

    /// <summary>中性色：岛体深色时是白色系，浅色（Fluent + 浅色系统）时是黑色系。</summary>
    private void ApplyThemeColors()
    {
        _textBrush.Color = Neutral(255);
        _tileBrush.Color = Neutral(26);        // 约 10% 透明的中性底
        _tileHoverBrush.Color = Neutral(45);   // 约 18% 透明的中性底（悬浮提示用）
    }

    private Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);

    /// <summary>
    /// 形态动画走「逐帧属性赋值」，刻意不用 Storyboard（原因见类注释与技能铁律 5）。
    /// </summary>
    private void StartMorph(bool expanded, TimeSpan duration)
    {
        var target = expanded ? 1d : 0d;

        _morphFrom = _progress;
        _morphTarget = target;
        _morphDuration = duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(1);
        _morphStart = DateTimeOffset.UtcNow;

        if (_morphTimer is null)
        {
            ApplyMorph(target);
            return;
        }

        _morphTimer.Start();
    }

    private void OnMorphTick()
    {
        try
        {
            var elapsed = (DateTimeOffset.UtcNow - _morphStart).TotalMilliseconds;
            var durationMs = Math.Max(1, _morphDuration.TotalMilliseconds);
            var t = Math.Clamp(elapsed / durationMs, 0, 1);

            ApplyMorph(_morphFrom + (_morphTarget - _morphFrom) * BackEaseOut(t));

            if (t >= 1)
            {
                _morphTimer?.Stop();
                ApplyMorph(_morphTarget);   // 收尾时锁定终态，避免残留中间值
            }
        }
        catch
        {
            // 定时器回调里的异常会直接计入宿主未处理异常，这里兜住
            _morphTimer?.Stop();
        }
    }

    /// <summary>BackEase(EaseOut, A) = 1 + (A+1)(t-1)³ + A(t-1)²，与 XAML 那条曲线等价。</summary>
    private static double BackEaseOut(double t)
    {
        var d = t - 1;
        return 1 + (BackAmplitude + 1) * d * d * d + BackAmplitude * d * d;
    }

    /// <summary>把 0~1 的形态进度铺到各元素上。progress 会因 BackEase 略微越过 0/1。</summary>
    private void ApplyMorph(double progress)
    {
        _progress = progress;
        progress = Math.Clamp(progress, 0, 1);

        // 命令框：Height 不能为负（BackEase 在终点附近会把曲线推到目标值以下）
        _commandBox.Height = Math.Max(0, CommandHeight * progress);
        _commandBox.Opacity = progress;

        // 图标方块与首字随形态缩放
        var tile = CompactTile + (ExpandedTile - CompactTile) * progress;
        var glyphSize = CompactGlyph + (ExpandedGlyph - CompactGlyph) * progress;
        foreach (var border in _tiles)
        {
            border.Width = tile;
            border.Height = tile;
            border.CornerRadius = new CornerRadius(tile / 2);
        }
        foreach (var glyph in _tileGlyphs)
        {
            glyph.FontSize = glyphSize;
        }
    }
}
