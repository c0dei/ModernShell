using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ModernShell.Core;
using ModernShell.Native;

namespace ModernShell.UI;

/// <summary>
/// IME の状態表示（純正タスクバーの「あ / A」と同じ）。前面のアプリの IME の状態を読み取って表示し、クリックで日本語入力のオン/オフを切り替える。
/// 純正タスクバーを隠すとこの表示も消えるため、ModernShell で同じものを出す。
/// </summary>
public sealed partial class TaskbarController
{
    private TextBlock? _imeText;
    private DispatcherTimer? _imeTimer;

    [DllImport("imm32.dll")] private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    private const uint WM_IME_CONTROL = 0x0283;
    private const int IMC_GETCONVERSIONMODE = 0x0001, IMC_GETOPENSTATUS = 0x0005, IMC_SETOPENSTATUS = 0x0006;
    private const int IME_CMODE_NATIVE = 0x1, IME_CMODE_KATAKANA = 0x2, IME_CMODE_FULLSHAPE = 0x8;

    private Tile ImeTile()
    {
        _imeText = UiKit.Label("", 13);
        _imeText.MinWidth = 16;
        _imeText.TextAlignment = TextAlignment.Center;
        var tile = new Tile
        {
            Padding = new Thickness(8, 0, 8, 0), Height = TileH, Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = _imeText, ToolTip = L.T("IME（クリックで日本語入力のオン/オフ）"),
        };
        // タスクバーはクリックしてもアクティブにならないので、前面のアプリの IME をそのまま切り替えられる
        tile.Click += _ => { ToggleIme(); UpdateIme(); };
        _imeTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => UpdateIme(), _dispatcher);
        _imeTimer.Start();
        UpdateIme();
        return tile;
    }

    /// <summary>前面のアプリの IME のウィンドウ（IME への問い合わせ先）とキーボードの言語。</summary>
    private static (IntPtr ime, int lang) ForegroundIme()
    {
        var fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return (IntPtr.Zero, 0);
        uint thread = Win32.GetWindowThreadProcessId(fg, out _);
        int lang = (int)((long)GetKeyboardLayout(thread) & 0xFFFF);
        return (ImmGetDefaultIMEWnd(fg), lang);
    }

    private static int ImeQuery(IntPtr ime, int command)
    {
        // 応答しないアプリで止まらないよう、短い時間で打ち切る
        return SendMessageTimeout(ime, WM_IME_CONTROL, (IntPtr)command, IntPtr.Zero, 0x2 /*SMTO_ABORTIFHUNG*/, 100, out var r) == IntPtr.Zero
            ? -1 : (int)(long)r;
    }

    private void UpdateIme()
    {
        if (_imeText == null || _disposed) return;
        var (ime, lang) = ForegroundIme();
        string text;
        if (lang != 0 && lang != 0x0411)
        {
            // 日本語以外のキーボード: 言語の略号（EN など）
            try { text = new CultureInfo(lang).TwoLetterISOLanguageName.ToUpperInvariant(); } catch { text = "--"; }
        }
        else
        {
            int open = ime != IntPtr.Zero ? ImeQuery(ime, IMC_GETOPENSTATUS) : -1;
            if (open < 0) return; // 読み取れない（管理者として動くアプリなど）ときは前の表示のまま
            int mode = open > 0 ? ImeQuery(ime, IMC_GETCONVERSIONMODE) : 0;
            if (mode < 0) mode = 0;
            bool full = (mode & IME_CMODE_FULLSHAPE) != 0;
            text = open == 0 ? "A"
                : (mode & IME_CMODE_NATIVE) != 0
                    ? (mode & IME_CMODE_KATAKANA) != 0 ? (full ? "カ" : "ｶ") : "あ"
                    : full ? "Ａ" : "A";
        }
        if (_imeText.Text != text) _imeText.Text = text;
    }

    private static void ToggleIme()
    {
        var (ime, lang) = ForegroundIme();
        if (ime == IntPtr.Zero || (lang != 0 && lang != 0x0411)) return;
        int open = ImeQuery(ime, IMC_GETOPENSTATUS);
        if (open < 0) return;
        SendMessageTimeout(ime, WM_IME_CONTROL, (IntPtr)IMC_SETOPENSTATUS, (IntPtr)(open > 0 ? 0 : 1), 0x2, 100, out _);
    }
}
