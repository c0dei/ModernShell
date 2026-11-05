using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ModernShell.Core;

namespace ModernShell.UI;

public sealed class SettingsWindow : Window
{
    private readonly Settings _s;
    public event Action? ExitRequested;

    public SettingsWindow(Settings settings)
    {
        _s = settings;
        Title = "ModernShell の設定";
        Width = 620;
        Height = 760;
        FontFamily = Theme.TextFont;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SourceInitialized += (_, _) => Build();
        Theme.Changed += () => Dispatcher.BeginInvoke(() => { if (IsLoaded) Build(); });
    }

    private void Build()
    {
        var t = Theme.Current;
        Backdrop.Apply(this, BackdropKind.Mica, t, alwaysInactive: false);
        Foreground = t.Text;

        var root = new StackPanel { Margin = new Thickness(28, 20, 28, 28) };
        root.Children.Add(H1("ModernShell"));
        root.Children.Add(Note("explorer.exe へのコード注入を行わない、別プロセス型のスタートメニュー / タスクバーです。"));

        // ---- スタート ----
        root.Children.Add(Section("スタートメニュー"));
        root.Children.Add(Toggle("Win キーで ModernShell のスタートを開く", _s.ReplaceStartMenu, v => _s.ReplaceStartMenu = v));
        root.Children.Add(Toggle("純正タスクバーのスタートボタンのクリックも置き換える", _s.InterceptNativeStartButton, v => _s.InterceptNativeStartButton = v));
        root.Children.Add(Toggle("開いたときに「すべてのアプリ」を表示", _s.ShowAllAppsFirst, v => _s.ShowAllAppsFirst = v));
        root.Children.Add(Choice("ピン留めの列数", new[] { "4", "5", "6", "7", "8" }, _s.PinnedColumns.ToString(), v => _s.PinnedColumns = int.Parse(v)));

        // ---- タスクバー ----
        root.Children.Add(Section("タスクバー"));
        root.Children.Add(Toggle("純正タスクバーを隠して ModernShell のタスクバーを使う", _s.UseCustomTaskbar, v => _s.UseCustomTaskbar = v));
        root.Children.Add(Note("※ 現バージョンの独自タスクバーには通知領域（システムトレイ）アイコンがありません。クイック設定 (Win+A)・通知 (Win+N) は利用できます。"));
        root.Children.Add(Choice("配置", new[] { "中央", "左" }, _s.TaskbarAlignment == TaskbarAlignment.Center ? "中央" : "左",
            v => _s.TaskbarAlignment = v == "中央" ? TaskbarAlignment.Center : TaskbarAlignment.Left));
        root.Children.Add(Choice("位置", new[] { "下", "上" }, _s.TaskbarEdge == TaskbarEdge.Bottom ? "下" : "上",
            v => _s.TaskbarEdge = v == "下" ? TaskbarEdge.Bottom : TaskbarEdge.Top));
        root.Children.Add(Choice("高さ", new[] { "40", "44", "48", "56", "64" }, _s.TaskbarHeight.ToString(), v => _s.TaskbarHeight = int.Parse(v)));
        root.Children.Add(Toggle("同じアプリのボタンをまとめる", _s.GroupTaskbarButtons, v => _s.GroupTaskbarButtons = v));
        root.Children.Add(Toggle("ボタンにウィンドウ名を表示", _s.ShowTaskbarLabels, v => _s.ShowTaskbarLabels = v));
        root.Children.Add(Toggle("時計に秒を表示", _s.ShowSeconds, v => _s.ShowSeconds = v));

        // ---- 外観 ----
        root.Children.Add(Section("外観"));
        root.Children.Add(Choice("テーマ", new[] { "システムに合わせる", "ライト", "ダーク" },
            _s.Theme switch { ThemeMode.Light => "ライト", ThemeMode.Dark => "ダーク", _ => "システムに合わせる" },
            v => _s.Theme = v switch { "ライト" => ThemeMode.Light, "ダーク" => ThemeMode.Dark, _ => ThemeMode.System }));
        root.Children.Add(Choice("背景効果", new[] { "Acrylic", "Mica", "Solid" }, _s.Backdrop.ToString(),
            v => _s.Backdrop = Enum.Parse<BackdropKind>(v)));
        root.Children.Add(TextRow("アクセントカラー (#RRGGBB、空欄でシステム)", _s.AccentColor, v => _s.AccentColor = v));
        root.Children.Add(Choice("角丸", new[] { "0", "4", "8", "12" }, ((int)_s.CornerRadius).ToString(), v => _s.CornerRadius = double.Parse(v)));

        // ---- 一般 ----
        root.Children.Add(Section("一般"));
        root.Children.Add(Toggle("サインイン時に起動", _s.RunAtStartup, v => { _s.RunAtStartup = v; SystemActions.SetRunAtStartup(v); }));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(Button("設定フォルダを開く", () => Process.Start("explorer.exe", "\"" + Settings.Directory + "\"")));
        buttons.Children.Add(Button("ModernShell を終了", () => ExitRequested?.Invoke()));
        root.Children.Add(buttons);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    // テーマの再適用は App が Settings.Changed を見て必要なときだけ行う
    private void Commit() => _s.Save();

    private static TextBlock H1(string text) => new() { Text = text, FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Theme.Current.Text, Margin = new Thickness(0, 0, 0, 4) };

    private static TextBlock Section(string text) => new()
    {
        Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Theme.Current.Text, Margin = new Thickness(0, 22, 0, 8),
    };

    private static TextBlock Note(string text) => new()
    {
        Text = text, FontSize = 12, Foreground = Theme.Current.TextSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6),
    };

    private static Border Card(FrameworkElement label, FrameworkElement control)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(label);
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return new Border
        {
            Background = Theme.Current.Surface, BorderBrush = Theme.Current.Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 2, 0, 2), Child = g,
        };
    }

    private Border Toggle(string text, bool value, Action<bool> set)
    {
        var cb = new CheckBox { IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
        cb.Checked += (_, _) => { set(true); Commit(); };
        cb.Unchecked += (_, _) => { set(false); Commit(); };
        var label = UiKit.Label(text, 13);
        label.TextWrapping = TextWrapping.Wrap;
        return Card(label, cb);
    }

    private Border Choice(string text, string[] options, string current, Action<string> set)
    {
        var combo = new ComboBox { MinWidth = 160, ItemsSource = options, SelectedItem = options.Contains(current) ? current : options[0] };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is string v) { set(v); Commit(); } };
        return Card(UiKit.Label(text, 13), combo);
    }

    private Border TextRow(string text, string value, Action<string> set)
    {
        var box = new TextBox { Text = value, MinWidth = 160, VerticalContentAlignment = VerticalAlignment.Center };
        box.LostFocus += (_, _) => { set(box.Text.Trim()); Commit(); };
        return Card(UiKit.Label(text, 13), box);
    }

    private static Button Button(string text, Action onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }
}
