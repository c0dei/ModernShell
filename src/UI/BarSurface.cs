using System;
using System.Windows;
using System.Windows.Interop;
using ModernShell.Core;
using ModernShell.Native;

namespace ModernShell.UI;

/// <summary>
/// タスクバーの「見た目」1 枚分。帯型なら 1 枚、アイランド型なら島ごとに 1 枚。
/// 島ごとに別ウィンドウにしているので、島の間はデスクトップがそのままクリックできる。
/// </summary>
public sealed class BarSurface : Window
{
    public enum Anchor { Fill, Left, Center, Right }

    public Anchor Placement { get; }

    private readonly Settings _settings;
    private SurfacePaint.Painted? _painted;
    public event Action? ContentSizeChanged;

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>
    /// 背景・角丸・枠線はすべて自分で描く（透過ウィンドウ）。Windows の背景効果は GPU が無いと描かれないため使わない。
    /// </summary>
    public BarSurface(Anchor placement, double heightDip, Settings settings)
    {
        Placement = placement;
        _settings = settings;
        Title = "ModernShell Taskbar";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        FontFamily = Theme.Current.UiFont;
        UseLayoutRounding = true;
        // 正しい位置に移動されるまで画面外に置いてチラつきを防ぐ
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;

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
        HwndSource.FromHwnd(hwnd)!.AddHook(WndProc);

        SizeChanged += (_, _) => { ContentSizeChanged?.Invoke(); PlaceBlur(); };
        ContentRendered += (_, _) => Log.Timing($"taskbar surface first drawn ({placement})");
        LocationChanged += (_, _) => PlaceBlur();
        if (settings.TaskbarBackground == SurfaceStyle.Blur)
        {
            MicaLike.Changed += PlaceBlur;
            Closed += (_, _) => MicaLike.Changed -= PlaceBlur;
        }
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

    /// <summary>中身を設定する。設定の背景（透過なし / 透過 / ぼかし）・角丸・枠線を付けて包む。</summary>
    public void SetBody(FrameworkElement content)
    {
        bool flush = Placement == Anchor.Fill && _settings.BandMargin <= 0; // 画面端に密着した帯は角を丸めない
        var corner = flush ? new CornerRadius(0) : new CornerRadius(Math.Max(0, _settings.CornerRadius));
        _painted = SurfacePaint.Wrap(content, _settings.TaskbarBackground, _settings.TaskbarOpacity, corner);
        Content = _painted.Root;
        PlaceBlur();
    }

    /// <summary>ぼかしの画像を、このウィンドウの今の画面上の位置に合わせて切り出し直す。</summary>
    private void PlaceBlur() => _painted?.Place(Handle);

    public void MoveTo(int x, int y, int? w = null, int? h = null, bool bottomMost = false)
    {
        uint flags = Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW;
        // 島は SizeToContent を壊さないよう位置だけ動かす
        if (w == null || h == null) flags |= Win32.SWP_NOSIZE;
        Win32.SetWindowPos(Handle, bottomMost ? new IntPtr(1) /*HWND_BOTTOM*/ : Win32.HWND_TOPMOST, x, y, w ?? 0, h ?? 0, flags);
        PlaceBlur();
    }
}
