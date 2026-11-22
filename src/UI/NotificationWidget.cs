using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ModernShell.Core;

namespace ModernShell.UI;

/// <summary>通知のウィジェットとポップアップで共有する部品。</summary>
internal static class NotificationLook
{
    public const string GlyphBell = "", GlyphMoon = "", GlyphClose = "", GlyphSettings = "", GlyphChevron = "", GlyphClear = "", GlyphCheck = "";

    /// <summary>バッジの色（赤みのある色。白い文字が読める濃さ）。</summary>
    public static readonly Brush Badge = Frozen(new SolidColorBrush(Color.FromRgb(0xE5, 0x5B, 0x4B)));

    private static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }

    /// <summary>「3分前」「昨日」のような経過時間。</summary>
    public static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return L.T("たった今");
        if (d.TotalMinutes < 60) return L.F("{0}分前", (int)d.TotalMinutes);
        if (d.TotalHours < 24 && t.Date == DateTime.Today) return L.F("{0}時間前", (int)d.TotalHours);
        if (t.Date == DateTime.Today.AddDays(-1)) return L.T("昨日");
        return t.ToString(L.IsEnglish ? "MMM d" : "M月d日", L.Culture);
    }

    /// <summary>アイコンが取れない通知元の、頭文字の札に使う色（名前から決める）。</summary>
    public static Color Tint(string name)
    {
        var palette = new[] { 0x7C8CFF, 0x4DC9A0, 0xF2B84B, 0xF28B82, 0x5BB5E8, 0xC58AF9 };
        int h = 0;
        foreach (var c in name) h = h * 31 + c;
        int rgb = palette[Math.Abs(h) % palette.Length];
        return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}

/// <summary>
/// 通知のウィジェット: ベルのアイコンと、通知の数のバッジ（応答不可のときは月のアイコン）。
/// クリックで、アプリの通知ポップアップ（通知の一覧・応答不可の切り替え・Windows の通知設定）か Windows の通知センターを開く。
/// </summary>
public sealed class NotificationBarWidget : BarWidget
{
    public override string Id => "notification";
    private TextBlock? _icon, _count;
    private Border? _badge;
    private string _sig = "";

    protected override FrameworkElement BuildContent()
    {
        double size = Vertical ? 17 : 19;
        _icon = UiKit.Glyph(NotificationLook.GlyphBell, size, T.Text);
        _count = UiKit.Label("", 10, Brushes.White, FontWeights.Bold);
        _count.TextTrimming = TextTrimming.None;
        _count.HorizontalAlignment = HorizontalAlignment.Center;
        _badge = new Border
        {
            MinWidth = 15, Height = 15, CornerRadius = new CornerRadius(8), Background = NotificationLook.Badge, Padding = new Thickness(3, 0, 3, 0),
            Child = _count, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -3, -7, 0), Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        var holder = new Grid { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Width = size, Height = size + 4 };
        holder.Children.Add(_icon);
        holder.Children.Add(_badge);

        holder.Loaded += (_, _) => { NotificationControl.Changed -= OnChanged; NotificationControl.Changed += OnChanged; _ = NotificationControl.RefreshCountAsync(); Refresh(); };
        holder.Unloaded += (_, _) => NotificationControl.Changed -= OnChanged;
        Refresh();
        if (Vertical) return holder;
        // 他のウィジェットと同じ、薄い地と縁のある角丸のカード
        holder.Margin = new Thickness(9, 0, 13, 0);
        PadOverride = new Thickness(0);
        return new Border { Background = T.Hover, BorderBrush = T.Stroke, BorderThickness = new Thickness(1), CornerRadius = T.Corner(6), Child = holder };
    }

    private void OnChanged()
    {
        var d = _icon?.Dispatcher;
        if (d == null) return;
        if (d.CheckAccess()) Refresh(); else d.BeginInvoke(new Action(Refresh));
    }

    public override void Tick(int count)
    {
        // 通知の数は 3 秒ごとに読み直す（OS からの知らせが届かないときの備え）
        if (count % 3 == 0) _ = NotificationControl.RefreshCountAsync();
        Refresh(); // 応答不可は設定画面などからも変わる
    }

    private void Refresh()
    {
        if (_icon == null || _badge == null || _count == null) return;
        bool dnd = NotificationControl.DoNotDisturb;
        int n = NotificationControl.Count;
        string sig = dnd + "|" + n;
        if (sig == _sig) return;
        _sig = sig;
        _icon.Text = dnd ? NotificationLook.GlyphMoon : NotificationLook.GlyphBell;
        _badge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        _count.Text = n > 99 ? "99+" : n.ToString();
        if (Card != null)
        {
            string tip = n < 0 ? L.T("通知") : n == 0 ? L.T("新しい通知はありません") : L.F("通知 {0} 件", n);
            Card.ToolTip = dnd ? tip + "\n" + L.T("応答不可: オン") : tip;
        }
    }

    private static ClickOpens Opens => (Settings.Current ?? new Settings()).NotificationWidgetOpens;

    public override bool OnClick() => ClickTarget.OpenWindowsIfChosen(Opens, Outer);

    public override bool OpensSystemPanel => Opens == ClickOpens.Windows;

    public override FrameworkElement? BuildDetailsView() => Opens == ClickOpens.App ? new NotificationDetailsView() : null;

    public override IEnumerable<MenuItemSpec> ExtraMenu()
    {
        bool dnd = NotificationControl.DoNotDisturb;
        var items = new List<MenuItemSpec>
        {
            new(dnd ? NotificationLook.GlyphCheck : "", dnd ? L.T("応答不可（Windows の設定で切り替え）") : L.T("応答不可にする（Windows の設定を開く）"), () => Launcher.Open(NotificationControl.SettingsUri)),
            new(NotificationLook.GlyphClear, L.T("すべての通知をクリア"), () => _ = NotificationControl.ClearAllAsync()),
            new(NotificationLook.GlyphSettings, L.T("通知の設定"), () => Launcher.Open(NotificationControl.SettingsUri)),
            MenuItemSpec.Separator,
        };
        items.AddRange(ClickTarget.Menu(Opens, v => { if (Settings.Current is { } c) { c.NotificationWidgetOpens = v; c.Save(); } }, Outer, L.T("アプリの通知")));
        return items;
    }
}

// ---------------------------------------------------------------------------

/// <summary>通知のポップアップ: 応答不可の切り替え・通知の一覧（1 件ずつ消せる）・すべてクリア・Windows の通知設定へのリンク。</summary>
internal sealed class NotificationDetailsView : Border
{
    private readonly Theme _t = Theme.Current;
    private readonly TextBlock _countLabel;
    private readonly Tile _clearAll;
    private readonly ToggleSwitch _dnd;
    private readonly StackPanel _list = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string _sig = "\0";
    private bool _busy;

    public NotificationDetailsView()
    {
        Width = 400;
        Padding = new Thickness(16, 14, 16, 10);
        System.Windows.Documents.TextElement.SetForeground(this, _t.Text);
        var root = new StackPanel();
        Child = root;

        // 見出し: 「通知  4件」 / すべてクリア
        var head = new Grid { Margin = new Thickness(4, 0, 0, 10) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(UiKit.Label(L.T("通知"), 17, _t.Text, FontWeights.SemiBold));
        _countLabel = UiKit.Label("", 12, _t.TextSecondary);
        _countLabel.Margin = new Thickness(8, 3, 0, 0);
        title.Children.Add(_countLabel);
        head.Children.Add(title);
        _clearAll = new Tile { Padding = new Thickness(10, 5, 10, 5), Child = UiKit.Label(L.T("すべてクリア"), 13, _t.TextSecondary) };
        _clearAll.Click += async _ => { await NotificationControl.ClearAllAsync(); _sig = "\0"; Reload(); };
        Grid.SetColumn(_clearAll, 1);
        head.Children.Add(_clearAll);
        root.Children.Add(head);

        // 応答不可
        _dnd = new ToggleSwitch(NotificationControl.DoNotDisturb, showLabel: false) { VerticalAlignment = VerticalAlignment.Center };
        // 切り替えは Windows の設定で行う。押したら、表示は実際の状態に戻して、設定を開く
        _dnd.Toggled += _ => { _dnd.IsOn = NotificationControl.DoNotDisturb; Launcher.Open(NotificationControl.SettingsUri); };
        var dndGrid = new Grid();
        dndGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dndGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dndGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dndGrid.Children.Add(IconBox(NotificationLook.GlyphMoon, _t.Hover, _t.Text, 36));
        var dndText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        dndText.Children.Add(UiKit.Label(L.T("応答不可"), 14, _t.Text, FontWeights.SemiBold));
        var dndSub = UiKit.Label(L.T("通知音とポップアップを止めます。切り替えは Windows の通知設定で"), 12, _t.TextSecondary);
        dndSub.TextWrapping = TextWrapping.Wrap; dndSub.TextTrimming = TextTrimming.None;
        dndText.Children.Add(dndSub);
        Grid.SetColumn(dndText, 1);
        dndGrid.Children.Add(dndText);
        Grid.SetColumn(_dnd, 2);
        dndGrid.Children.Add(_dnd);
        root.Children.Add(new Border { Background = _t.Hover, CornerRadius = _t.Corner(10), Padding = new Thickness(12, 10, 12, 10), Child = dndGrid });

        // 通知の一覧（多いときはスクロール）
        root.Children.Add(new ScrollViewer
        {
            MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0),
            Content = _list,
        });

        // Windows の通知設定
        var settings = new Tile { Padding = new Thickness(10), Margin = new Thickness(0, 8, 0, 0) };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(UiKit.Glyph(NotificationLook.GlyphSettings, 15, _t.TextSecondary));
        var txt = UiKit.Label(L.T("Windows の通知の設定"), 13);
        txt.Margin = new Thickness(12, 0, 8, 0);
        Grid.SetColumn(txt, 1);
        row.Children.Add(txt);
        var chev = UiKit.Glyph(NotificationLook.GlyphChevron, 11, _t.TextSecondary);
        Grid.SetColumn(chev, 2);
        row.Children.Add(chev);
        settings.Child = row;
        settings.Click += _ => Launcher.Open(NotificationControl.SettingsUri);
        root.Children.Add(new Border { Height = 1, Background = _t.Stroke, Margin = new Thickness(2, 8, 2, 0) });
        root.Children.Add(settings);

        _timer.Tick += (_, _) => { _dnd.IsOn = NotificationControl.DoNotDisturb; Reload(); };
        Loaded += (_, _) => { _timer.Start(); Reload(); };
        Unloaded += (_, _) => _timer.Stop();
        BuildList(null);
    }

    private static Border IconBox(string glyph, Brush bg, Brush fg, double size) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(10), Background = bg, VerticalAlignment = VerticalAlignment.Center,
        Child = UiKit.Glyph(glyph, size * 0.45, fg),
    };

    private async void Reload()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var items = await NotificationControl.GetAsync();
            // 変わっていないときは作り直さない（スクロール位置やホバーを保つ）
            string sig = items == null ? "null" : string.Join(",", items.Select(i => i.Id)) + "|" + (items.Count > 0 ? NotificationLook.Ago(items[0].Time) : "");
            if (sig != _sig) { _sig = sig; BuildList(items); }
        }
        catch (Exception ex) { Log.Error("notification view", ex); }
        finally { _busy = false; }
    }

    private void BuildList(List<NotificationItem>? items)
    {
        _list.Children.Clear();
        _countLabel.Text = items == null ? "" : L.F("{0}件", items.Count);
        _clearAll.Visibility = items is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (items == null)
        {
            // 通知へのアクセスが許可されていない: 許可する場所を案内する
            var msg = UiKit.Label(L.T("通知の一覧を表示するには、Windows の設定で「通知へのアクセス」を許可してください"), 12, _t.TextSecondary);
            msg.TextWrapping = TextWrapping.Wrap; msg.TextTrimming = TextTrimming.None;
            var open = new Tile { Padding = new Thickness(10), Margin = new Thickness(0, 6, 0, 0), Child = UiKit.Label(L.T("通知へのアクセスの設定を開く"), 13, _t.AccentBrush) };
            open.Click += _ => Launcher.Open(NotificationControl.AccessSettingsUri);
            _list.Children.Add(new StackPanel { Margin = new Thickness(4, 12, 4, 6), Children = { msg, open } });
            return;
        }
        if (items.Count == 0)
        {
            var empty = UiKit.Label(L.T("新しい通知はありません"), 13, _t.TextSecondary);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Margin = new Thickness(0, 28, 0, 28);
            _list.Children.Add(empty);
            return;
        }
        foreach (var item in items) _list.Children.Add(Card(item));
    }

    private FrameworkElement Card(NotificationItem n)
    {
        // アイコン: 通知元アプリのもの（取れるまでは、頭文字の札）
        string initial = string.IsNullOrEmpty(n.AppName) ? "?" : n.AppName[..1].ToUpperInvariant();
        var tint = NotificationLook.Tint(n.AppName);
        var badge = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(0x38, tint.R, tint.G, tint.B)),
            Child = UiKit.Label(initial, 15, new SolidColorBrush(tint), FontWeights.Bold),
        };
        ((TextBlock)badge.Child).HorizontalAlignment = HorizontalAlignment.Center;
        _ = LoadLogo(n, badge);

        var meta = UiKit.Label(string.IsNullOrEmpty(n.AppName) ? NotificationLook.Ago(n.Time) : n.AppName + "  ・  " + NotificationLook.Ago(n.Time), 12, _t.TextSecondary);
        var texts = new StackPanel { Margin = new Thickness(12, 0, 8, 0) };
        texts.Children.Add(meta);
        if (n.Title.Length > 0)
        {
            var t = UiKit.Label(n.Title, 14, _t.Text, FontWeights.SemiBold);
            t.Margin = new Thickness(0, 2, 0, 0);
            texts.Children.Add(t);
        }
        if (n.Body.Length > 0)
        {
            var b = UiKit.Label(n.Body, 13, _t.TextSecondary);
            b.TextWrapping = TextWrapping.Wrap; b.TextTrimming = TextTrimming.CharacterEllipsis; b.MaxHeight = 56;
            b.Margin = new Thickness(0, 2, 0, 0);
            texts.Children.Add(b);
        }

        var close = new Tile { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(14), Child = UiKit.Glyph(NotificationLook.GlyphClose, 11, _t.TextSecondary), ToolTip = L.T("消去") };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(badge);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);

        var card = new Border { Background = _t.Hover, CornerRadius = _t.Corner(10), Padding = new Thickness(12, 10, 8, 12), Margin = new Thickness(0, 0, 0, 6), Child = grid };
        close.Click += _ =>
        {
            NotificationControl.Remove(n.Id);
            _list.Children.Remove(card);
            _sig = "\0"; // 次の読み直しで、件数の表示なども合わせ直す
            Reload();
        };
        return card;
    }

    private static async System.Threading.Tasks.Task LoadLogo(NotificationItem n, Border badge)
    {
        try
        {
            var bmp = await NotificationControl.LogoAsync(n);
            if (bmp == null) return;
            badge.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80));
            badge.Child = UiKit.Icon(bmp, 24);
        }
        catch { }
    }
}
