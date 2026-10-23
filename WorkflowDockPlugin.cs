using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinIsland.Core;

namespace WorkflowDock;

/// <summary>一个快捷入口：显示名 + 启动目标（exe 名称 / 完整路径 / 网址等）。</summary>
public sealed record AppEntry(string Label, string Target);

/// <summary>
/// 工作流坞插件入口。一个插件包有且只有一个 IIslandPlugin 实现，
/// 必须是 public sealed 且有公共无参构造函数，否则宿主会拒绝加载。
///
/// 第一版功能：
///   - 小岛常驻一排快捷图标（来自设置，默认记事本/计算器/画图/资源管理器），点击启动；
///   - 展开后多一个命令框：网址、文件/文件夹路径、快捷方式名称，回车执行；
///   - 设置页：显示开关 + App 列表编辑（一行一个：名称|目标）。
/// </summary>
public sealed class WorkflowDockPlugin : IslandPluginBase
{
    private const string DefaultApps = "记事本|notepad.exe\n计算器|calc.exe\n画图|mspaint.exe\n资源管理器|explorer.exe";

    private WorkflowDockView _view = null!;
    private IslandLiveContent _content = null!;

    protected override Task OnInitializeAsync()
    {
        Log.Info($"启动：{Manifest.Id} {Manifest.Version}，插件目录 {PluginDirectory}");

        _view = new WorkflowDockView(Manifest, Theme, LaunchApp, RunCommand);

        _content = new IslandLiveContent
        {
            Priority = Settings.Get("priority", 60),
            OwnerLabel = Manifest.Name,
            OwnerGlyph = Manifest.IconGlyph,
            OwnerAccent = Windows.UI.Color.FromArgb(255, 0x4C, 0xC2, 0xFF),
            MorphView = _view,
            CompactSize = new Windows.Foundation.Size(240, 40),
            ExpandedSize = new Windows.Foundation.Size(420, 120),
        };

        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            Manifest.Id, Manifest.Name, Manifest.IconGlyph ?? "\uE713", BuildSettingsPage, order: 100));

        // App 列表变更 → 重建图标
        Context.Register(Context.OnSettingsChanged("apps", () => _view.ReloadApps(LoadApps())));

        // 显示开关实时生效
        Context.Register(Context.OnSettingsChanged("enabled", () =>
        {
            SetContent(Settings.Get("enabled", true) ? _content : null);
        }));

        if (Settings.Get("enabled", true))
        {
            SetContent(_content);
        }

        return Task.CompletedTask;
    }

    protected override Task OnShutdownAsync()
    {
        Log.Info("已停用");
        SetContent(null);
        return Task.CompletedTask;
    }

    // ---- 数据 ----

    /// <summary>解析设置里的 App 列表：一行一个「名称|目标」，目标为空或格式错的行跳过。</summary>
    private List<AppEntry> LoadApps()
    {
        var list = new List<AppEntry>();
        var raw = Settings.Get("apps", DefaultApps) ?? DefaultApps;

        foreach (var line in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var text = line.Trim();
            if (text.Length == 0) continue;

            var sep = text.IndexOf('|');
            var label = sep > 0 ? text[..sep].Trim() : text;
            var target = sep > 0 ? text[(sep + 1)..].Trim() : text;

            if (label.Length == 0 || target.Length == 0) continue;
            list.Add(new AppEntry(label, target));
        }

        return list;
    }

    // ---- 启动 / 命令 ----

    private void LaunchApp(AppEntry app) => Open(app.Target, app.Label);

    /// <summary>
    /// 命令框回车后的解析（UI 线程调用）：
    ///   1. 与 App 列表里的名称完全匹配 → 启动那个 App；
    ///   2. 是网址（http/https/file/mailto）→ 用默认浏览器/处理程序打开；
    ///   3. shell: / ms-settings: 开头 → 交给系统；
    ///   4. 是存在的文件或文件夹 → 打开；
    ///   5. 都不是 → 提示无法识别。
    /// </summary>
    private string RunCommand(string input)
    {
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;

        foreach (var app in LoadApps())
        {
            if (string.Equals(app.Label, text, StringComparison.OrdinalIgnoreCase))
            {
                return Open(app.Target, app.Label);
            }
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "file" or "mailto")
        {
            return Open(text, uri.Host.Length > 0 ? uri.Host : text);
        }

        if (text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            return Open(text, text);
        }

        if (File.Exists(text) || Directory.Exists(text))
        {
            return Open(text, Path.GetFileName(text.TrimEnd('\\')) is { Length: > 0 } name ? name : text);
        }

        Log.Warn($"无法识别的命令：{text}");
        ShowFeedback("无法识别", $"「{text}」不是网址、路径，也不在快捷列表里", "\uE783");
        return string.Empty;
    }

    /// <summary>用系统默认方式打开目标。成功失败都弹一条岛上的临时提示。</summary>
    private string Open(string target, string label)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            Log.Info($"已打开 {label}: {target}");
            ShowFeedback("已打开", label, Manifest.IconGlyph ?? "\uE8A7");
            return string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error($"打开失败 {label}: {ex.Message}");
            ShowFeedback("打开失败", $"{label}：{ex.Message}", "\uE783");
            return string.Empty;
        }
    }

    private void ShowFeedback(string title, string text, string glyph)
    {
        try
        {
            Context.Island.ShowMessage(new IslandMessage
            {
                Title = title,
                Text = text,
                Glyph = glyph,
                AccentColor = Windows.UI.Color.FromArgb(255, 0x4C, 0xC2, 0xFF),
                Duration = TimeSpan.FromSeconds(2),
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"弹提示失败（已忽略）：{ex.Message}");
        }
    }

    // ---- 设置页 ----

    /// <summary>把多行输入框里的 App 列表写进设置。值没变就不写，避免多余的通知。</summary>
    private void CommitApps(TextBox box)
    {
        var text = (box.Text ?? string.Empty).Trim();
        if (string.Equals(text, Settings.Get("apps", DefaultApps), StringComparison.Ordinal)) return;

        Settings.Set("apps", text);   // 触发 OnSettingsChanged("apps") → 图标重建
    }

    /// <summary>设置页工厂：每次用户点进页面都会调用一次，必须返回新实例。</summary>
    private UIElement BuildSettingsPage()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = Manifest.Name,
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
        });

        var toggle = new ToggleSwitch
        {
            Header = "在灵动岛上显示",
            IsOn = Settings.Get("enabled", true),
        };
        toggle.Toggled += (_, _) => Settings.Set("enabled", toggle.IsOn);

        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = toggle,
        });

        // App 列表：多行编辑。提交点 = 失焦 + 「保存列表」按钮（多行框回车用来换行，不作为提交）
        var appsBox = new TextBox
        {
            Header = "快捷启动列表",
            Text = Settings.Get("apps", DefaultApps),
            PlaceholderText = "记事本|notepad.exe",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 160,
            MinWidth = 340,
        };
        appsBox.LostFocus += (_, _) => CommitApps(appsBox);

        var saveButton = new Button { Content = "保存列表" };
        saveButton.Click += (_, _) => CommitApps(appsBox);   // 动作之前先提交（同一动作）

        var hint = new TextBlock
        {
            Text = "每行一个快捷方式，格式：名称|目标。\n目标可以是 exe 名称（如 notepad.exe）、完整路径、文件夹路径或网址。\n小岛最多显示前 6 个，鼠标悬浮图标可看名称。",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        };

        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = new StackPanel { Spacing = 8, Children = { appsBox, saveButton, hint } },
        });

        return panel;
    }
}
