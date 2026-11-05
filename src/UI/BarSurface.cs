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
    public event Action? ContentSizeChanged;

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    public BarSurface(Anchor placement, double heightDip, BackdropKind backdrop)
    {
        Placement = placement;
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
        WindowChrome.SetWindowChrome(this, new WindowChrome
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
        Backdrop.Apply(this, backdrop, Theme.Current, alwaysInactive: true);

        SizeChanged += (_, _) => ContentSizeChanged?.Invoke();
    }

    public double Scale => Win32.GetDpiForWindow(Handle) is var d and > 0 ? d / 96.0 : 1.0;

    /// <summary>現在の大きさ（物理ピクセル）。</summary>
    public (int w, int h) PixelSize => ((int)Math.Round(ActualWidth * Scale), (int)Math.Round(ActualHeight * Scale));

    public void MoveTo(int x, int y, int? w = null, int? h = null, bool bottomMost = false)
    {
        uint flags = Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW;
        // 島は SizeToContent を壊さないよう位置だけ動かす
        if (w == null || h == null) flags |= Win32.SWP_NOSIZE;
        Win32.SetWindowPos(Handle, bottomMost ? new IntPtr(1) /*HWND_BOTTOM*/ : Win32.HWND_TOPMOST, x, y, w ?? 0, h ?? 0, flags);
    }
}
