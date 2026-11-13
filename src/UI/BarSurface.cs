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
    /// <summary>Left / Center / Right = 端に沿った方向の「始まり・中央・終わり」。縦のバーでは 上・中央・下。</summary>
    public enum Anchor { Fill, Left, Center, Right }

    public Anchor Placement { get; }

    private readonly Settings _settings;
    private SurfacePaint.Painted? _painted;
    public event Action? ContentSizeChanged;

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>
    /// 背景・角丸・枠線はすべて自分で描く（透過ウィンドウ）。Windows の背景効果は GPU が無いと描かれないため使わない。
    /// </summary>
    /// <param name="thicknessDip">バーの太さ（横のバーなら高さ、縦のバーなら幅）。</param>
    public BarSurface(Anchor placement, double thicknessDip, Settings settings, bool vertical = false)
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
            // 島は中身の長さに合わせて伸縮（Mac のドックのようにアプリ数で長さが変わる）。横なら幅、縦なら高さ
            if (vertical) { SizeToContent = SizeToContent.Height; Width = thicknessDip; }
            else { SizeToContent = SizeToContent.Width; Height = thicknessDip; }
        }

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        long ex = (long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
        // TOOLWINDOW: Alt+Tab に出さない / NOACTIVATE: クリックしても前面ウィンドウを奪わない（最小化トグル判定に必須）
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, (IntPtr)(ex | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE));
        // WPF はタスクバーのボタン一覧に出さないウィンドウに、見えない「親ウィンドウ」を付ける。
        // 「デスクトップの表示」(Win+D) はその親ウィンドウも最小化し、親が最小化されると子のタスクバーも一緒に隠れてしまう。
        // 親から切り離し（TOOLWINDOW なので一覧には出ない）、デスクトップのプレビュー（Aero Peek）でも透けないようにする
        Win32.SetWindowLongPtr(hwnd, -8 /*GWLP_HWNDPARENT*/, IntPtr.Zero);
        int excluded = 1;
        Win32.DwmSetWindowAttribute(hwnd, 12 /*DWMWA_EXCLUDED_FROM_PEEK*/, ref excluded, sizeof(int));
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
        bool flush = Placement == Anchor.Fill && _settings.BandMarginX <= 0 && _settings.BandMarginY <= 0; // 画面端に密着した帯は角を丸めない
        var corner = flush ? new CornerRadius(0) : new CornerRadius(Math.Max(0, _settings.CornerRadius));
        _painted = SurfacePaint.Wrap(content, _settings.TaskbarBackground, _settings.TaskbarOpacity, corner);
        Content = _painted.Root;
        PlaceBlur();
    }

    /// <summary>ぼかしの画像を、このウィンドウの今の画面上の位置に合わせて切り出し直す。</summary>
    private void PlaceBlur() => _painted?.Place(Handle);

    // 自動的に隠すとき用: 配置した位置からのずれ（物理ピクセル）と、隠している間は表示しない印
    private int _lastX, _lastY;
    private int? _lastW, _lastH;
    private bool _lastBottomMost;
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public bool Hidden { get; private set; }

    public void MoveTo(int x, int y, int? w = null, int? h = null, bool bottomMost = false)
    {
        _lastX = x; _lastY = y; _lastW = w; _lastH = h; _lastBottomMost = bottomMost;
        if (Hidden) return;
        Reposition();
    }

    /// <summary>最後に指定された位置（＋ずれ）へ動かす。</summary>
    public void Reposition()
    {
        uint flags = Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW;
        // 島は SizeToContent を壊さないよう位置だけ動かす
        if (_lastW == null || _lastH == null) flags |= Win32.SWP_NOSIZE;
        Win32.SetWindowPos(Handle, _lastBottomMost ? new IntPtr(1) /*HWND_BOTTOM*/ : Win32.HWND_TOPMOST, _lastX + OffsetX, _lastY + OffsetY, _lastW ?? 0, _lastH ?? 0, flags);
        PlaceBlur();
    }

    /// <summary>隠す（ウィンドウごと非表示）。隠している間は MoveTo しても表示しない。</summary>
    public void HideBar()
    {
        Hidden = true;
        Win32.ShowWindow(Handle, Win32.SW_HIDE);
    }

    /// <summary>隠すのをやめる（ずれは呼び出し側で決めてから Reposition する）。</summary>
    public void ShowBar()
    {
        Hidden = false;
        Reposition();
    }
}
