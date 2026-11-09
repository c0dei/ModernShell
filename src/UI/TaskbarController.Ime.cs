using System;
using System.Collections.Generic;
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
    private const int IMC_GETCONVERSIONMODE = 0x0001, IMC_SETCONVERSIONMODE = 0x0002, IMC_GETOPENSTATUS = 0x0005, IMC_SETOPENSTATUS = 0x0006;
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
        // 右クリックで入力モードのメニュー（純正タスクバーの IME メニューの主な項目）
        tile.RightClick += x => Flyout.Show(x, ImeMenu(), FlyoutPlacement);
        // 変化のきっかけ（IME に関係するキー・前面のアプリの切り替え）ですぐ読み直す。
        // マウスで IME を切り替えた場合などの取りこぼしに備えて、1 秒ごとにも読み直す
        _imeTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateIme(), _dispatcher);
        _imeTimer.Start();
        UpdateIme();
        return tile;
    }

    private DispatcherTimer? _imeSoon, _imeLater;

    /// <summary>問い合わせに答えないアプリの、キー操作から推測した日本語入力のオン/オフ（アプリのウィンドウごと）。</summary>
    private readonly Dictionary<IntPtr, bool> _inferredOpen = new();

    /// <summary>
    /// 日本語入力を切り替えるキーが押された。前面のアプリが IME の問い合わせに答えるなら何もしない（実際の状態を読む）。
    /// 答えないアプリなら、キーに合わせてオン/オフを推測して覚える。マウスでの切り替えなどはわからないので、ずれることがある。
    /// </summary>
    public void ImeToggleKey(uint vk)
    {
        if (_imeText == null || _disposed) return;
        var (ime, lang) = ForegroundIme();
        if (lang != 0 && lang != 0x0411) return;
        if (ime != IntPtr.Zero && ImeQuery(ime, IMC_GETOPENSTATUS) >= 0) return; // 読めるアプリは推測しない
        var fg = Win32.GetForegroundWindow();
        _inferredOpen.TryGetValue(fg, out var open);
        _inferredOpen[fg] = vk switch { 0x16 => true, 0x1A => false, _ => !open };
        if (_inferredOpen.Count > 200) _inferredOpen.Clear(); // 閉じたウィンドウの分が溜まらないように
        UpdateIme();
    }

    /// <summary>
    /// IME の状態が変わったかもしれないとき（キー操作・アプリの切り替え）に呼ぶ。IME はキーを受け取ってから少し遅れて
    /// 状態を変えるので、少し待って読み、念のためもう一度読む。
    /// </summary>
    public void ImeMayHaveChanged()
    {
        if (_imeText == null || _disposed) return;
        _imeSoon ??= new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Normal, (s, _) => { ((DispatcherTimer)s!).Stop(); UpdateIme(); }, _dispatcher);
        _imeLater ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (s, _) => { ((DispatcherTimer)s!).Stop(); UpdateIme(); }, _dispatcher);
        _imeSoon.Stop(); _imeSoon.Start();
        _imeLater.Stop(); _imeLater.Start();
    }

    /// <summary>前面のアプリの IME のウィンドウ（IME への問い合わせ先）とキーボードの言語。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public Win32.RECT rcCaret;
    }

    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);

    /// <summary>
    /// 今キーボードの入力を受け取っているウィンドウ（前面のアプリの入力欄）の IME と、そのスレッドのキーボードの言語。
    /// IME の状態は入力を受け取っているウィンドウごとなので、アプリの枠（一番外側のウィンドウ）ではなく、ここに問い合わせる。
    /// Windows Terminal や秀丸のように、入力欄が枠とは別の子ウィンドウ・別のスレッドになっているアプリでも正しく読める。
    /// </summary>
    private static (IntPtr ime, int lang) ForegroundIme()
    {
        var fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return (IntPtr.Zero, 0);
        // スレッド 0 = 前面のスレッド。入力を受け取っているウィンドウ（なければ前面のウィンドウ）
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        var target = GetGUIThreadInfo(0, ref info) && info.hwndFocus != IntPtr.Zero ? info.hwndFocus : fg;
        uint thread = Win32.GetWindowThreadProcessId(target, out _);
        int lang = (int)((long)GetKeyboardLayout(thread) & 0xFFFF);
        var ime = ImmGetDefaultIMEWnd(target);
        // 入力欄の IME が答えないときは、アプリの枠の IME にも聞いてみる（アプリによってはこちらが答える）
        if (target != fg && (ime == IntPtr.Zero || ImeQuery(ime, IMC_GETOPENSTATUS) < 0))
        {
            var top = ImmGetDefaultIMEWnd(fg);
            if (top != IntPtr.Zero && ImeQuery(top, IMC_GETOPENSTATUS) >= 0) ime = top;
        }
        return (ime, lang);
    }

    private static IntPtr _imeLoggedFor;

    /// <summary>前面のアプリが変わったとき、どのウィンドウの IME を読んで何が返ったかを 1 度だけ記録する（表示が変わらないアプリの調査用）。</summary>
    private static void LogImeTarget(IntPtr ime, int lang, int open, int mode)
    {
        var fg = Win32.GetForegroundWindow();
        if (fg == _imeLoggedFor) return;
        _imeLoggedFor = fg;
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        GetGUIThreadInfo(0, ref info);
        string Cls(IntPtr h) { var sb = new System.Text.StringBuilder(128); if (h != IntPtr.Zero) GetClassName(h, sb, sb.Capacity); return sb.ToString(); }
        Log.Info($"ime: app={Cls(fg)} focus={Cls(info.hwndFocus)} imeWnd={(ime != IntPtr.Zero)} lang=0x{lang:X4} open={open} mode=0x{mode:X}" +
                 (open < 0 ? $" error={_imeLastError} os={Environment.OSVersion.Version}" : ""));
    }

    /// <summary>直前の問い合わせが失敗したときの Windows のエラー番号（5 = 管理者のアプリへの問い合わせを拒否、1460 = 時間切れ）。</summary>
    private static int _imeLastError;

    /// <summary>答えなかった IME のウィンドウと、次に問い合わせてよい時刻（答えないところに毎回待たされてタスクバーが固まらないように）。</summary>
    private static readonly Dictionary<IntPtr, DateTime> _imeSilentUntil = new();

    private static int ImeQuery(IntPtr ime, int command)
    {
        if (_imeSilentUntil.TryGetValue(ime, out var until) && DateTime.Now < until) return -1;
        // 応答しないアプリで止まらないよう、短い時間で打ち切る
        if (SendMessageTimeout(ime, WM_IME_CONTROL, (IntPtr)command, IntPtr.Zero, 0x2 /*SMTO_ABORTIFHUNG*/, 300, out var r) == IntPtr.Zero)
        {
            _imeLastError = Marshal.GetLastWin32Error();
            if (_imeSilentUntil.Count > 100) _imeSilentUntil.Clear();
            _imeSilentUntil[ime] = DateTime.Now.AddSeconds(10);
            return -1;
        }
        _imeSilentUntil.Remove(ime);
        return (int)(long)r;
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
            int mode = open > 0 ? ImeQuery(ime, IMC_GETCONVERSIONMODE) : 0;
            LogImeTarget(ime, lang, open, mode);
            if (open < 0)
            {
                // 問い合わせに答えないアプリ（Windows Terminal・管理者として動くアプリなど）は、キー操作から推測した状態を出す
                open = _inferredOpen.TryGetValue(Win32.GetForegroundWindow(), out var o) && o ? 1 : 0;
                mode = open > 0 ? IME_CMODE_NATIVE | IME_CMODE_FULLSHAPE : 0;
            }
            if (mode < 0) mode = 0;
            bool full = (mode & IME_CMODE_FULLSHAPE) != 0;
            text = open == 0 ? "A"
                : (mode & IME_CMODE_NATIVE) != 0
                    ? (mode & IME_CMODE_KATAKANA) != 0 ? (full ? "カ" : "ｶ") : "あ"
                    : full ? "Ａ" : "A";
        }
        if (_imeText.Text != text) _imeText.Text = text;
    }

    /// <summary>
    /// 入力モードのメニュー。モードの切り替えは前面のアプリの IME に直接頼む（タスクバーもメニューもアクティブにならないので、
    /// 前面のアプリはそのまま）。「半角英数」は日本語入力をオフにする（純正の「A」と同じ）。
    /// </summary>
    private IEnumerable<MenuItemSpec> ImeMenu()
    {
        var (ime, lang) = ForegroundIme();
        bool usable = ime != IntPtr.Zero && (lang == 0 || lang == 0x0411);
        int open = usable ? ImeQuery(ime, IMC_GETOPENSTATUS) : -1;
        int mode = open > 0 ? ImeQuery(ime, IMC_GETCONVERSIONMODE) : 0;
        const int H = IME_CMODE_NATIVE | IME_CMODE_FULLSHAPE, K = IME_CMODE_NATIVE | IME_CMODE_KATAKANA | IME_CMODE_FULLSHAPE,
            A = IME_CMODE_FULLSHAPE, k = IME_CMODE_NATIVE | IME_CMODE_KATAKANA, Mask = IME_CMODE_NATIVE | IME_CMODE_KATAKANA | IME_CMODE_FULLSHAPE;
        int current = open > 0 ? mode & Mask : -1;

        // メニューを開いた時点の前面のアプリの IME に対して切り替える（メニューの操作中に前面が変わっても対象を間違えない）
        MenuItemSpec Mode(string text, int value) =>
            new(current == value ? "\uE73E" : "", text, () => SetImeMode(ime, value)) { Enabled = usable };
        var items = new List<MenuItemSpec>
        {
            Mode(L.T("ひらがな"), H),
            Mode(L.T("全角カタカナ"), K),
            Mode(L.T("全角英数"), A),
            Mode(L.T("半角カタカナ"), k),
            new(open == 0 ? "\uE73E" : "", L.T("半角英数"), () => SetImeMode(ime, -1)) { Enabled = usable },
            MenuItemSpec.Separator,
            new("\uE713", L.T("IME の設定"), () =>
            {
                try { Launcher.Open("ms-settings:regionlanguage-jpnime"); }
                catch (Exception ex) { Log.Error("ime settings", ex); }
            }),
        };
        return items;
    }

    /// <summary>入力モードを変える。value が -1 なら日本語入力をオフ（半角英数）。</summary>
    private void SetImeMode(IntPtr ime, int value)
    {
        if (ime == IntPtr.Zero) return;
        if (value < 0)
            SendMessageTimeout(ime, WM_IME_CONTROL, (IntPtr)IMC_SETOPENSTATUS, IntPtr.Zero, 0x2, 100, out _);
        else
        {
            SendMessageTimeout(ime, WM_IME_CONTROL, (IntPtr)IMC_SETOPENSTATUS, (IntPtr)1, 0x2, 100, out _);
            SendMessageTimeout(ime, WM_IME_CONTROL, (IntPtr)IMC_SETCONVERSIONMODE, (IntPtr)value, 0x2, 100, out _);
        }
        ImeMayHaveChanged();
    }

    private void ToggleIme()
    {
        var (ime, lang) = ForegroundIme();
        if (lang != 0 && lang != 0x0411) return;
        int open = ime != IntPtr.Zero ? ImeQuery(ime, IMC_GETOPENSTATUS) : -1;
        if (open >= 0)
        {
            SendMessageTimeout(ime, WM_IME_CONTROL, (IntPtr)IMC_SETOPENSTATUS, (IntPtr)(open > 0 ? 0 : 1), 0x2, 100, out _);
            return;
        }
        // 問い合わせに答えないアプリ（Windows Terminal など）には、日本語入力の切り替えキー（漢字キー）を送って切り替え、
        // 推測している状態も合わせて切り替える（自分で送ったキーはキーボード監視で数えないため）
        Input.Send((0x19, false), (0x19, true));
        var fg = Win32.GetForegroundWindow();
        _inferredOpen.TryGetValue(fg, out var o);
        _inferredOpen[fg] = !o;
    }
}
