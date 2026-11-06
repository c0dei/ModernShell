using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using ModernShell.Core;
using ModernShell.Native;

namespace ModernShell.UI;

/// <summary>
/// タスクバーの「見た目」1 枚分。帯型なら 1 枚、アイランド型なら島ごとに 1 枚。
/// 島を別ウィンドウにしているのは、島ごとに本物の Acrylic と DWM の角丸を付けるため
/// （1 枚の透過ウィンドウでは部分的なぼかしができない）。島の間はデスクトップがそのままクリックできる。
/// </summary>
public sealed class BarSurface : Window
{
    public enum Anchor { Fill, Left, Center, Right }

    public Anchor Placement { get; }

    /// <summary>
    /// 自前で描く方式（「角丸を強制する」オン時）。透過ウィンドウにして、角丸と背景の透明度を WPF で描く。
    /// GPU が無いなど Windows がぼかし・透過・角丸を描かない環境でも見た目が変わらない（ぼかしは無し）。
    /// </summary>
    private readonly bool _selfDrawn;
    /// <summary>「Mica 風」: 壁紙をぼかした画像を自前で描く（<see cref="MicaLike"/>）。</summary>
    private readonly bool _micaLike;
    private System.Windows.Media.ImageBrush? _micaBrush;
    private readonly Settings _settings;
    public event Action? ContentSizeChanged;

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    public BarSurface(Anchor placement, double heightDip, Settings settings)
    {
        Placement = placement;
        _settings = settings;
        _selfDrawn = settings.ForceRoundCorners;
        _micaLike = settings.TaskbarMaterial == TaskbarMaterial.MicaLike;
        Title = "ModernShell Taskbar";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        FontFamily = Theme.TextFont;
        UseLayoutRounding = true;
        // 正しい位置に移動されるまで画面外に置いてチラつきを防ぐ
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        if (_selfDrawn)
        {
            AllowsTransparency = true;
            Background = System.Windows.Media.Brushes.Transparent;
        }
        else WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(-1),
        });

        if (placement != Anchor.Fill)
        {
            // 島は中身の幅に合わせて伸縮（Mac のドックのようにアプリ数で幅が変わる）
            SizeToContent = SizeToContent.Width;
            Height = heightDip;
        }

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        long ex = (long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
        // TOOLWINDOW: Alt+Tab に出さない / NOACTIVATE: クリックしても前面ウィンドウを奪わない（最小化トグル判定に必須）
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, (IntPtr)(ex | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE));
        // タスクバーは常に非アクティブなので Windows 標準の背景効果は使えない（単色化する）。下地は設定の素材、濃さは重ねる色で付ける
        if (!_selfDrawn)
            // タスクバーの背景は「タスクバーの背景」設定だけで決める（スタートメニューの背景設定とは独立）
            // Mica 風は下地を自分で描くので、DWM の効果は使わない（単色の上に壁紙の画像を敷く）
            Backdrop.Apply(this, settings.TaskbarMaterial is TaskbarMaterial.Solid or TaskbarMaterial.MicaLike ? BackdropKind.Solid : BackdropKind.Acrylic,
                BackdropEngine.AccentBlur, Theme.Current, alwaysInactive: true,
                tint: settings.TaskbarOpacity / 100.0, material: settings.TaskbarMaterial);
        HwndSource.FromHwnd(hwnd)!.AddHook(WndProc);

        SizeChanged += (_, _) => { ContentSizeChanged?.Invoke(); PlaceMica(); };
        if (_micaLike)
        {
            LocationChanged += (_, _) => PlaceMica();
            MicaLike.Changed += PlaceMica;
            Closed += (_, _) => MicaLike.Changed -= PlaceMica;
        }
        if (!_selfDrawn) Backdrop.KeepRegion(this, settings);
    }

    private const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;

    /// <summary>
    /// クリックされてもアクティブにならないようにする。タスクバーがアクティブ化を奪うと
    /// 「前面のウィンドウをクリック → 最小化」の判定が崩れるため（WS_EX_NOACTIVATE だけでは WPF が奪うことがある）。
    /// </summary>
    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return (IntPtr)MA_NOACTIVATE;
        }
        return IntPtr.Zero;
    }

    public double Scale => Win32.GetDpiForWindow(Handle) is var d and > 0 ? d / 96.0 : 1.0;

    /// <summary>現在の大きさ（物理ピクセル）。</summary>
    public (int w, int h) PixelSize => ((int)Math.Round(ActualWidth * Scale), (int)Math.Round(ActualHeight * Scale));

    /// <summary>中身を設定する。自前で描く方式なら、角丸・半透明の背景・枠線を付けて包む。</summary>
    public void SetBody(FrameworkElement content)
    {
        if (!_selfDrawn && !_micaLike) { Content = content; return; }
        var t = Theme.Current;
        // 透明度 0 でもクリックが裏へ抜けないよう、ほんの少しだけ色を残す
        byte alpha = (byte)Math.Max(1, Math.Round(Math.Clamp(_settings.TaskbarOpacity, 0, 100) / 100.0 * 255));
        var c = t.SolidColor;
        bool flush = Placement == Anchor.Fill && _settings.BandMargin <= 0; // 画面端に密着した帯は角を丸めない
        // 自前で描く方式なら角丸に描く。そうでなければ角丸は Windows（DWM）に任せる
        var corner = _selfDrawn && !flush ? new CornerRadius(Math.Max(0, _settings.CornerRadius)) : new CornerRadius(0);
        var tint = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, c.R, c.G, c.B));
        if (_micaLike)
        {
            // 下: 壁紙をぼかした画像（自分の位置の部分）/ 上: 不透明度の設定どおりの色。画像ができるまでは単色
            _micaBrush = new System.Windows.Media.ImageBrush
            {
                ViewboxUnits = System.Windows.Media.BrushMappingMode.Absolute, Stretch = System.Windows.Media.Stretch.Fill,
            };
            content = new System.Windows.Controls.Border { Background = tint, CornerRadius = corner, Child = content };
            Content = new System.Windows.Controls.Border
            {
                CornerRadius = corner, Background = t.SolidBackground, ClipToBounds = true,
                BorderBrush = _selfDrawn ? t.Stroke : null, BorderThickness = new Thickness(_selfDrawn ? 1 : 0),
                Child = new System.Windows.Controls.Border { CornerRadius = corner, Background = _micaBrush, Child = content },
            };
            PlaceMica();
            return;
        }
        Content = new System.Windows.Controls.Border
        {
            CornerRadius = corner, Background = tint, BorderBrush = t.Stroke, BorderThickness = new Thickness(1), Child = content,
        };
    }

    /// <summary>Mica 風の画像を、このウィンドウの今の画面上の位置に合わせて切り出し直す。</summary>
    private void PlaceMica()
    {
        if (_micaBrush == null || !Win32.GetWindowRect(Handle, out var r) || r.Width <= 0) return;
        var bar = new System.Drawing.Rectangle(r.Left, r.Top, r.Width, r.Height);
        var monitor = System.Windows.Forms.Screen.FromRectangle(bar).Bounds;
        MicaLike.Place(_micaBrush, bar, monitor);
    }

    /// <summary>今の大きさに合わせて「角丸を強制する」の切り抜きを更新する。</summary>
    public void UpdateRegion(Settings settings)
    {
        if (_selfDrawn) return; // 自前で描く方式は切り抜き不要（なめらかな角丸を WPF が描く）
        if (Win32.GetWindowRect(Handle, out var r)) Backdrop.UpdateRegion(Handle, r.Width, r.Height, settings, Scale);
    }

    public void MoveTo(int x, int y, int? w = null, int? h = null, bool bottomMost = false)
    {
        uint flags = Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW;
        // 島は SizeToContent を壊さないよう位置だけ動かす
        if (w == null || h == null) flags |= Win32.SWP_NOSIZE;
        Win32.SetWindowPos(Handle, bottomMost ? new IntPtr(1) /*HWND_BOTTOM*/ : Win32.HWND_TOPMOST, x, y, w ?? 0, h ?? 0, flags);
        PlaceMica();
    }
}
