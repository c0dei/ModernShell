using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ModernShell.Core;
using ModernShell.Native;

namespace ModernShell.UI;

/// <summary>
/// Everything の検索結果を出す、アプリ独自のウィンドウ（Everything SDK で検索する）。
/// 画面の中央（上寄り）に出る。ほかをクリックするか Esc で閉じる。
/// </summary>
public sealed class EverythingWindow : Window
{
    private const double BodyW = 920, BodyH = 640, DetailW = 300;
    private const int MaxHits = 200;

    private readonly Settings _settings;
    private bool _closed, _dirty = true;
    private SurfacePaint.Painted? _painted;
    private DateTime _hiddenAt = DateTime.MinValue;

    private TextBox _box = null!;
    private TextBlock _placeholder = null!;
    private Border _boxFrame = null!;
    private StackPanel _rows = null!;
    private ScrollViewer _scroll = null!;
    private ContentControl _detail = null!;
    private TextBlock _statusLeft = null!, _statusRight = null!;
    private Ellipse? _dot;
    private readonly List<Border> _chipEls = new();
    private readonly List<Border> _toggleEls = new();
    private readonly List<TextBlock> _headEls = new();

    private EverythingFilter _filter = EverythingFilter.All;
    private bool _case, _regex, _path;
    private EverythingSort _sort = EverythingSort.NameAsc;
    private List<EverythingHit> _hits = new();
    private readonly List<Tile> _tiles = new();
    private int _sel = -1;
    private int _gen;
    private readonly DispatcherTimer _debounce;

    public EverythingWindow(Settings settings)
    {
        _settings = settings;
        Title = "ModernShell Everything";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        Width = BodyW + SurfacePaint.ShadowPad * 2;
        Height = BodyH + SurfacePaint.ShadowPad * 2;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;

        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => { _debounce!.Stop(); RunQuery(); }, Dispatcher);
        _debounce.Stop();

        Closed += (_, _) => _closed = true;
        Deactivated += (_, _) => HidePanel();
        PreviewKeyDown += OnPreviewKeyDown;
        Action onTheme = () => Dispatcher.BeginInvoke(() => _dirty = true);
        Theme.Changed += onTheme;
        Closed += (_, _) => Theme.Changed -= onTheme;

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        SurfacePaint.PassShadowClicks(HwndSource.FromHwnd(hwnd)!);
        long ex = (long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, (IntPtr)(ex | Win32.WS_EX_TOOLWINDOW));
        LocationChanged += (_, _) => _painted?.Place(Handle);
        MicaLike.Changed += PlaceBlur;
        Closed += (_, _) => MicaLike.Changed -= PlaceBlur;
    }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;
    private void PlaceBlur() { if (!_closed) _painted?.Place(Handle); }

    // ------------------------------------------------------------------
    // 表示

    /// <summary>開いていれば閉じる。閉じていれば開く。</summary>
    public void Toggle()
    {
        if (IsVisible) { HidePanel(); return; }
        if ((DateTime.Now - _hiddenAt).TotalMilliseconds < 250) return;
        ShowSearch(null);
    }

    /// <summary>開いて、検索ボックスにフォーカスを当てる（query があれば、その言葉で検索する）。</summary>
    public void ShowSearch(string? query)
    {
        if (_closed) return;
        if (_dirty) { Rebuild(); _dirty = false; }
        if (query != null) _box.Text = query;
        else if (!IsVisible) _box.Text = "";

        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        double scale = Win32.GetDpiForWindow(Handle) is var d and > 0 ? d / 96.0 : 1.0;
        double w = Width * scale, h = Height * scale;
        double x = screen.Left + (screen.Width - w) / 2;
        double y = screen.Top + Math.Max(0, (screen.Height - h) * 0.28);
        Left = x / scale;
        Top = y / scale;

        if (!IsVisible) Show();
        Win32.SetWindowPos(Handle, IntPtr.Zero, (int)x, (int)y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        _painted?.Place(Handle);
        Foregrounder.Force(Handle);
        Activate();
        _box.Focus();
        _box.CaretIndex = _box.Text.Length;
        _debounce.Stop();
        RunQuery();
    }

    public void HidePanel()
    {
        if (!IsVisible) return;
        _hiddenAt = DateTime.Now;
        _gen++; // 返ってくる途中の検索結果は捨てる
        Hide();
    }

    // ------------------------------------------------------------------
    // 組み立て

    private void Rebuild()
    {
        var t = Theme.Current;
        Foreground = t.Text;
        FontFamily = t.UiFont;
        _chipEls.Clear(); _toggleEls.Clear(); _headEls.Clear(); _tiles.Clear();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = SearchRow(t);
        top.Margin = new Thickness(16, 16, 16, 10);
        root.Children.Add(top);

        var chips = ChipRow(t);
        chips.Margin = new Thickness(16, 0, 16, 12);
        Grid.SetRow(chips, 1);
        root.Children.Add(chips);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DetailW) });
        var list = ListArea(t);
        body.Children.Add(list);
        _detail = new ContentControl { Margin = new Thickness(20, 16, 20, 16) };
        var detailHost = new Border { BorderBrush = t.Stroke, BorderThickness = new Thickness(1, 0, 0, 0), Child = _detail };
        Grid.SetColumn(detailHost, 1);
        body.Children.Add(detailHost);
        var bodyHost = new Border { BorderBrush = t.Stroke, BorderThickness = new Thickness(0, 1, 0, 0), Child = body };
        Grid.SetRow(bodyHost, 2);
        root.Children.Add(bodyHost);

        var status = StatusBar(t);
        Grid.SetRow(status, 3);
        root.Children.Add(status);

        _painted = SurfacePaint.Wrap(root, _settings.StartMenuBackground, _settings.StartMenuOpacity, new CornerRadius(Math.Max(0, _settings.CornerRadius) + 4), shadow: true);
        Content = _painted.Root;
        UpdateChips();
        UpdateToggles();
        ShowDetail();
        ShowEmpty(L.T("検索する言葉を入力してください"));
    }

    private FrameworkElement SearchRow(Theme t)
    {
        var icon = SearchLook.FolderSearchIcon(20, t.TextSecondary);
        icon.Margin = new Thickness(14, 0, 10, 0);
        _box = new TextBox
        {
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = t.Text, CaretBrush = t.Text,
            FontSize = 16, VerticalContentAlignment = VerticalAlignment.Center,
        };
        _placeholder = UiKit.Label(L.T("ファイル名・フォルダー名で検索"), 16, t.TextSecondary);
        _placeholder.IsHitTestVisible = false;
        _placeholder.Margin = new Thickness(2, 0, 0, 0);
        _box.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _debounce.Stop(); _debounce.Start();
        };
        var boxHost = new Grid();
        boxHost.Children.Add(_placeholder);
        boxHost.Children.Add(_box);

        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        toggles.Children.Add(Toggle(t, "Aa", L.T("大文字と小文字を区別"), () => { _case = !_case; }, () => _case));
        toggles.Children.Add(Toggle(t, ".*", L.T("正規表現"), () => { _regex = !_regex; }, () => _regex));
        toggles.Children.Add(Toggle(t, L.T("パス"), L.T("フォルダーのパスも検索"), () => { _path = !_path; }, () => _path));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        Grid.SetColumn(boxHost, 1);
        grid.Children.Add(boxHost);
        Grid.SetColumn(toggles, 2);
        grid.Children.Add(toggles);

        _boxFrame = new Border { Height = 52, CornerRadius = t.Corner(10), Background = t.Surface, BorderBrush = t.Stroke, BorderThickness = new Thickness(1), Child = grid };
        _box.GotKeyboardFocus += (_, _) => _boxFrame.BorderBrush = t.AccentBrush;
        _box.LostKeyboardFocus += (_, _) => _boxFrame.BorderBrush = t.Stroke;
        return _boxFrame;
    }

    private FrameworkElement Toggle(Theme t, string text, string tip, Action flip, Func<bool> isOn)
    {
        var label = UiKit.Label(text, 13, t.Text, FontWeights.SemiBold);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var b = new Border
        {
            MinWidth = 34, Height = 30, Margin = new Thickness(3, 0, 0, 0), Padding = new Thickness(8, 0, 8, 0), CornerRadius = t.Corner(7),
            BorderThickness = new Thickness(1), Child = label, ToolTip = tip, Cursor = Cursors.Hand, Tag = isOn,
        };
        b.MouseLeftButtonUp += (_, _) => { flip(); UpdateToggles(); _debounce.Stop(); _debounce.Start(); _box.Focus(); };
        _toggleEls.Add(b);
        return b;
    }

    private void UpdateToggles()
    {
        var t = Theme.Current;
        foreach (var b in _toggleEls)
        {
            bool on = ((Func<bool>)b.Tag)();
            b.Background = on ? new SolidColorBrush(Color.FromArgb(0x55, t.Accent.R, t.Accent.G, t.Accent.B)) : t.Hover;
            b.BorderBrush = on ? t.AccentBrush : t.Stroke;
        }
    }

    private static string FilterName(EverythingFilter f) => f switch
    {
        EverythingFilter.Folder => L.T("フォルダー"),
        EverythingFilter.Document => L.T("ドキュメント"),
        EverythingFilter.Image => L.T("画像"),
        EverythingFilter.Video => L.T("動画"),
        EverythingFilter.Audio => L.T("音声"),
        EverythingFilter.Archive => L.T("圧縮"),
        EverythingFilter.Executable => L.T("実行ファイル"),
        _ => L.T("すべて"),
    };

    private FrameworkElement ChipRow(Theme t)
    {
        var row = new WrapPanel();
        foreach (var f in Enum.GetValues<EverythingFilter>())
        {
            var label = UiKit.Label(FilterName(f), 13, t.Text, FontWeights.SemiBold);
            var chip = new Border
            {
                Height = 32, Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(16),
                BorderThickness = new Thickness(1), Child = label, Cursor = Cursors.Hand, Tag = f,
            };
            chip.MouseLeftButtonUp += (_, _) => { _filter = f; UpdateChips(); RunQuery(); _box.Focus(); };
            _chipEls.Add(chip);
            row.Children.Add(chip);
        }
        return row;
    }

    private void UpdateChips()
    {
        var t = Theme.Current;
        foreach (var c in _chipEls)
        {
            bool on = (EverythingFilter)c.Tag == _filter;
            c.Background = on ? t.AccentBrush : Brushes.Transparent;
            c.BorderBrush = on ? t.AccentBrush : t.Stroke;
            ((TextBlock)c.Child).Foreground = on ? t.OnAccent : t.Text;
        }
    }

    private FrameworkElement ListArea(Theme t)
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var head = new Grid { Margin = new Thickness(16, 8, 16, 4) };
        foreach (var w in new[] { 44.0, 0, 96, 116 })
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        void H(int col, string text, EverythingSort asc, EverythingSort desc, HorizontalAlignment align)
        {
            var tb = UiKit.Label(text, 12, t.TextSecondary);
            tb.HorizontalAlignment = align; tb.Cursor = Cursors.Hand; tb.Tag = (text, asc, desc);
            tb.MouseLeftButtonUp += (_, _) => { _sort = _sort == asc ? desc : asc; UpdateHeads(); RunQuery(); _box.Focus(); };
            Grid.SetColumn(tb, col);
            head.Children.Add(tb);
            _headEls.Add(tb);
        }
        H(1, L.T("名前"), EverythingSort.NameAsc, EverythingSort.NameDesc, HorizontalAlignment.Left);
        H(2, L.T("サイズ"), EverythingSort.SizeDesc, EverythingSort.SizeAsc, HorizontalAlignment.Right);
        H(3, L.T("更新日時"), EverythingSort.DateDesc, EverythingSort.DateAsc, HorizontalAlignment.Right);
        UpdateHeads();
        g.Children.Add(head);

        _rows = new StackPanel { Margin = new Thickness(8, 0, 8, 8) };
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _rows };
        Grid.SetRow(_scroll, 1);
        g.Children.Add(_scroll);
        return g;
    }

    private void UpdateHeads()
    {
        var t = Theme.Current;
        foreach (var h in _headEls)
        {
            var (text, asc, desc) = ((string, EverythingSort, EverythingSort))h.Tag;
            bool on = _sort == asc || _sort == desc;
            h.Text = text + (on ? (_sort == asc ? " ▲" : " ▼") : "");
            h.Foreground = on ? t.Text : t.TextSecondary;
        }
    }

    private FrameworkElement StatusBar(Theme t)
    {
        _statusLeft = UiKit.Label("", 13, t.TextSecondary);
        _statusRight = UiKit.Label("", 13, t.TextSecondary);
        _dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0xD1, 0x9A)), Visibility = Visibility.Collapsed };
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _dot, _statusRight } };
        var g = new Grid { Margin = new Thickness(20, 10, 20, 12) };
        g.Children.Add(_statusLeft);
        g.Children.Add(right);
        return new Border { BorderBrush = t.Stroke, BorderThickness = new Thickness(0, 1, 0, 0), Child = g };
    }

    // ------------------------------------------------------------------
    // 検索

    private void RunQuery()
    {
        if (!IsVisible) return;
        string text = _box.Text.Trim();
        int gen = ++_gen;
        if (text.Length == 0 && _filter == EverythingFilter.All)
        {
            SetHits(new List<EverythingHit>());
            ShowEmpty(L.T("検索する言葉を入力してください"));
            _statusLeft.Text = "";
            _statusRight.Text = "";
            _dot!.Visibility = Visibility.Collapsed;
            return;
        }
        var (f, c, r, p, s) = (_filter, _case, _regex, _path, _sort);
        _ = Task.Run(() => EverythingSdk.Query(text, f, c, r, p, s, MaxHits)).ContinueWith(task =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_closed || gen != _gen) return; // 新しい検索が始まっている
                Apply(task.IsCompletedSuccessfully ? task.Result : new(new(), 0, 0, "error"));
            });
        });
    }

    private void Apply(EverythingResult res)
    {
        if (res.Error != null)
        {
            SetHits(new List<EverythingHit>());
            ShowEmpty(res.Error switch
            {
                "notfound" => L.T("Everything が見つかりません。Everything をインストールしてください"),
                "ipc" => L.T("Everything に接続できません。Everything を起動してから、もう一度試してください"),
                "sdk" => L.T("Everything SDK（Everything64.dll）を読み込めません"),
                _ => L.T("検索できませんでした"),
            });
            _statusLeft.Text = "";
            _statusRight.Text = "";
            _dot!.Visibility = Visibility.Collapsed;
            return;
        }
        SetHits(res.Hits);
        if (res.Hits.Count == 0) ShowEmpty(L.T("一致するファイルはありません"));
        _statusLeft.Text = res.Total > res.Hits.Count
            ? L.F("{0} 件中 {1} 件を表示・{2} ms", res.Total.ToString("N0"), res.Hits.Count, res.Ms)
            : L.F("{0} 件・{1} ms", res.Hits.Count, res.Ms);
        _statusRight.Text = L.T("Everything と接続中");
        _dot!.Visibility = Visibility.Visible;
    }

    private void ShowEmpty(string message)
    {
        _rows.Children.Clear();
        _tiles.Clear();
        var t = UiKit.Label(message, 14, Theme.Current.TextSecondary);
        t.TextWrapping = TextWrapping.Wrap;
        t.TextTrimming = TextTrimming.None;
        t.Margin = new Thickness(12, 24, 12, 0);
        _rows.Children.Add(t);
    }

    private void SetHits(List<EverythingHit> hits)
    {
        _hits = hits;
        _rows.Children.Clear();
        _tiles.Clear();
        var t = Theme.Current;
        for (int i = 0; i < hits.Count; i++)
        {
            int index = i;
            var tile = BuildRow(t, hits[i]);
            tile.Click += _ => Select(index);
            tile.PreviewMouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { Select(index); OpenSelected(false); e.Handled = true; } };
            _tiles.Add(tile);
            _rows.Children.Add(tile);
        }
        _scroll.ScrollToTop();
        _sel = -1;
        Select(hits.Count > 0 ? 0 : -1);
    }

    private Tile BuildRow(Theme t, EverythingHit h)
    {
        var g = new Grid();
        foreach (var w in new[] { 44.0, 0, 96, 116 })
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        var badge = Badge(h, 32, 10);
        g.Children.Add(badge);
        var name = UiKit.Label(h.Name, 15, t.Text);
        var dir = UiKit.Label(h.Dir, 12, t.TextSecondary);
        dir.TextTrimming = TextTrimming.CharacterEllipsis;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0), Children = { name, dir } };
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        var size = UiKit.Label(h.IsFolder ? "—" : FormatSize(h.Size), 13, t.Text);
        size.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(size, 2);
        g.Children.Add(size);
        var date = UiKit.Label(FormatDate(h.Modified), 13, t.Text);
        date.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(date, 3);
        g.Children.Add(date);
        return new Tile { Child = g, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 1, 0, 1), ToolTip = h.FullPath };
    }

    private void Select(int index)
    {
        if (_sel >= 0 && _sel < _tiles.Count) _tiles[_sel].IsSelected = false;
        _sel = index;
        if (_sel >= 0 && _sel < _tiles.Count)
        {
            _tiles[_sel].IsSelected = true;
            _tiles[_sel].BringIntoView();
        }
        ShowDetail();
    }

    // ------------------------------------------------------------------
    // 右の詳細

    private void ShowDetail()
    {
        var t = Theme.Current;
        if (_sel < 0 || _sel >= _hits.Count) { _detail.Content = null; return; }
        var h = _hits[_sel];
        var (kind, _, _) = Category(h);

        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var info = new StackPanel();
        var big = Badge(h, 64, 18);
        big.HorizontalAlignment = HorizontalAlignment.Left;
        big.Margin = new Thickness(0, 0, 0, 16);
        info.Children.Add(big);
        var name = UiKit.Label(h.Name, 18, t.Text, FontWeights.SemiBold);
        name.TextWrapping = TextWrapping.Wrap; name.TextTrimming = TextTrimming.None;
        info.Children.Add(name);
        var path = UiKit.Label(h.Dir, 13, t.TextSecondary);
        path.TextWrapping = TextWrapping.Wrap; path.TextTrimming = TextTrimming.None; path.Margin = new Thickness(0, 6, 0, 16);
        info.Children.Add(path);
        var facts = new Grid();
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Fact(string k, string v)
        {
            int r = facts.RowDefinitions.Count;
            facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var kl = UiKit.Label(k, 14, t.TextSecondary); kl.Margin = new Thickness(0, 4, 0, 4);
            var vl = UiKit.Label(v, 14, t.Text); vl.Margin = new Thickness(0, 4, 0, 4);
            Grid.SetRow(kl, r); Grid.SetRow(vl, r); Grid.SetColumn(vl, 1);
            facts.Children.Add(kl); facts.Children.Add(vl);
        }
        Fact(L.T("種類"), kind);
        if (!h.IsFolder) Fact(L.T("サイズ"), FormatSize(h.Size));
        if (h.Modified != DateTime.MinValue) Fact(L.T("更新"), h.Modified.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture));
        info.Children.Add(facts);
        panel.Children.Add(info);

        var buttons = new StackPanel();
        buttons.Children.Add(Button(t, L.T("開く"), true, () => OpenSelected(false)));
        buttons.Children.Add(Button(t, L.T("フォルダーを開く"), false, () => OpenSelected(true)));
        var copy = UiKit.Label(L.T("パスをコピー"), 14, t.Text);
        var copyBtn = new Tile { Child = copy, Padding = new Thickness(0, 10, 0, 10), Margin = new Thickness(0, 4, 0, 0) };
        copy.HorizontalAlignment = HorizontalAlignment.Center;
        copyBtn.Click += _ => { try { Clipboard.SetText(h.FullPath); } catch (Exception ex) { Log.Error("copy path", ex); } };
        buttons.Children.Add(copyBtn);
        Grid.SetRow(buttons, 1);
        panel.Children.Add(buttons);
        _detail.Content = panel;
    }

    private static FrameworkElement Button(Theme t, string text, bool primary, Action onClick)
    {
        var label = UiKit.Label(text, 15, primary ? t.OnAccent : t.Text, FontWeights.SemiBold);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var tile = new Tile
        {
            Child = label, Padding = new Thickness(0, 11, 0, 11), Margin = new Thickness(0, 0, 0, 8), CornerRadius = t.Corner(8),
        };
        if (primary) { tile.Background = t.AccentBrush; tile.BorderBrush = t.AccentBrush; }
        else tile.BorderBrush = t.Stroke;
        tile.Click += _ => onClick();
        return tile;
    }

    // ------------------------------------------------------------------
    // 操作

    private void OpenSelected(bool folder)
    {
        if (_sel < 0 || _sel >= _hits.Count) return;
        var h = _hits[_sel];
        try
        {
            if (folder && !h.IsFolder) Launcher.Open("explorer.exe", $"/select,\"{h.FullPath}\"");
            else Launcher.Open(h.FullPath);
        }
        catch (Exception ex) { Log.Error("everything open", ex); }
        HidePanel();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (_box.Text.Length > 0 && Keyboard.FocusedElement == _box) _box.Text = "";
                else HidePanel();
                e.Handled = true;
                break;
            case Key.Down when _tiles.Count > 0:
                Select(Math.Min(_tiles.Count - 1, _sel + 1));
                e.Handled = true;
                break;
            case Key.Up when _tiles.Count > 0:
                Select(Math.Max(0, _sel - 1));
                e.Handled = true;
                break;
            case Key.Enter when _sel >= 0:
                OpenSelected(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
                break;
        }
    }

    // ------------------------------------------------------------------
    // 部品・書式

    private static (string Kind, string Badge, Color Color) Category(EverythingHit h)
    {
        if (h.IsFolder) return (L.T("フォルダー"), "DIR", Color.FromRgb(0xF2, 0xC9, 0x4C));
        string ext = h.Extension.ToLowerInvariant();
        string badge = h.Extension.Length is > 0 and <= 4 ? h.Extension : h.Extension.Length > 4 ? h.Extension[..4] : "FILE";
        return ext switch
        {
            "zip" or "7z" or "rar" or "gz" or "tar" or "cab" or "iso" => (L.T("圧縮ファイル"), badge, Color.FromRgb(0xA0, 0xA6, 0xB8)),
            "mp4" or "mkv" or "avi" or "mov" or "wmv" or "webm" => (L.T("動画"), badge, Color.FromRgb(0xF2, 0x99, 0x4A)),
            "mp3" or "wav" or "flac" or "m4a" or "aac" or "ogg" => (L.T("音声"), badge, Color.FromRgb(0xBB, 0x6B, 0xD9)),
            "jpg" or "jpeg" or "png" or "gif" or "webp" or "bmp" or "svg" or "heic" => (L.T("画像"), badge, Color.FromRgb(0xE0, 0x6C, 0xA8)),
            "exe" or "msi" or "bat" or "cmd" or "ps1" or "com" => (L.T("実行ファイル"), badge, Color.FromRgb(0x3F, 0xCF, 0x9A)),
            "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "pdf" or "txt" or "md" or "rtf" or "csv" or "odt" => (L.T("ドキュメント"), badge, Color.FromRgb(0x5B, 0x8D, 0xEF)),
            "log" => (L.T("ログ"), badge, Color.FromRgb(0x5B, 0x8D, 0xEF)),
            _ => (L.T("ファイル"), badge, Color.FromRgb(0x9A, 0xA4, 0xB8)),
        };
    }

    private static FrameworkElement Badge(EverythingHit h, double size, double radius)
    {
        var (_, text, c) = Category(h);
        var label = new TextBlock
        {
            Text = text, FontSize = size * (text.Length > 3 ? 0.27 : 0.32), FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(c),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(radius * size / 32 * 0.6 + 4),
            Background = new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B)), Child = label,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 0) return "—";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {u[i]}";
    }

    private static string FormatDate(DateTime d)
    {
        if (d == DateTime.MinValue) return "";
        return d.Year == DateTime.Now.Year ? d.ToString("MM/dd HH:mm", CultureInfo.InvariantCulture) : d.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
    }
}
