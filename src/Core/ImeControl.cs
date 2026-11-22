using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using ModernShell.Native;

namespace ModernShell.Core;

/// <summary>日本語入力の今の状態。</summary>
public enum ImeMode { Hiragana, Katakana, HalfKatakana, FullAlpha, HalfAlpha, Direct }

/// <param name="Text">バーに出す 1 文字（あ / カ / ｶ / Ａ / A、日本語以外のキーボードでは言語の略号）</param>
/// <param name="Japanese">前面のキーボードが日本語か（そうでなければ切り替えは効かない）</param>
/// <param name="Usable">IME に問い合わせて切り替えられるか（IME のウィンドウが見つかる日本語入力のとき）</param>
public sealed record ImeState(string Text, bool Japanese, bool Usable, ImeMode Mode, IntPtr Ime);

/// <summary>
/// 前面のアプリの IME の読み取りと切り替え。バーの IME 表示・IME のウィジェット・ポップアップが共有する。
/// IME の状態は、キーボードの入力を受け取っているウィンドウごとに持たれているので、そこへ問い合わせる。
/// </summary>
public static class ImeControl
{
    [DllImport("imm32.dll")] private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);

    private const uint WM_IME_CONTROL = 0x0283;
    private const int IMC_GETCONVERSIONMODE = 0x0001, IMC_SETCONVERSIONMODE = 0x0002, IMC_GETOPENSTATUS = 0x0005, IMC_SETOPENSTATUS = 0x0006;
    private const int NATIVE = 0x1, KATAKANA = 0x2, FULLSHAPE = 0x8, ROMAN = 0x10;

    /// <summary>IME の状態が変わったかもしれない（キー操作・アプリの切り替えなど）。表示を読み直す合図。</summary>
    public static event Action? MayHaveChanged;
    public static void NotifyMayHaveChanged() => MayHaveChanged?.Invoke();

    /// <summary>問い合わせに答えないアプリの、キー操作から推測した日本語入力のオン/オフ（アプリのウィンドウごと）。</summary>
    private static readonly Dictionary<IntPtr, bool> InferredOpen = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public Win32.RECT rcCaret;
    }

    private static readonly Dictionary<int, string> LangCode = new();

    /// <summary>
    /// 今キーボードの入力を受け取っているウィンドウ（前面のアプリの入力欄）の IME と、そのスレッドのキーボードの言語。
    /// Windows Terminal や秀丸のように、入力欄が枠とは別の子ウィンドウ・別のスレッドになっているアプリでも正しく読める。
    /// </summary>
    private static (IntPtr ime, int lang) ForegroundIme()
    {
        var fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return (IntPtr.Zero, 0);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        var target = GetGUIThreadInfo(0, ref info) && info.hwndFocus != IntPtr.Zero ? info.hwndFocus : fg;
        uint thread = Win32.GetWindowThreadProcessId(target, out _);
        int lang = (int)((long)GetKeyboardLayout(thread) & 0xFFFF);
        var ime = ImmGetDefaultIMEWnd(target);
        // 入力欄の IME が答えないときは、アプリの枠の IME にも聞いてみる（アプリによってはこちらが答える）
        if (target != fg && (ime == IntPtr.Zero || Query(ime, IMC_GETOPENSTATUS) < 0))
        {
            var top = ImmGetDefaultIMEWnd(fg);
            if (top != IntPtr.Zero && Query(top, IMC_GETOPENSTATUS) >= 0) ime = top;
        }
        return (ime, lang);
    }

    private static IntPtr _loggedFor;

    /// <summary>前面のアプリが変わったとき、どのウィンドウの IME を読んで何が返ったかを 1 度だけ記録する（表示が変わらないアプリの調査用）。</summary>
    private static void LogTarget(IntPtr ime, int lang, int open, int mode)
    {
        var fg = Win32.GetForegroundWindow();
        if (fg == _loggedFor) return;
        _loggedFor = fg;
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        GetGUIThreadInfo(0, ref info);
        string Cls(IntPtr h) { var sb = new System.Text.StringBuilder(128); if (h != IntPtr.Zero) GetClassName(h, sb, sb.Capacity); return sb.ToString(); }
        Log.Info($"ime: app={Cls(fg)} focus={Cls(info.hwndFocus)} imeWnd={(ime != IntPtr.Zero)} lang=0x{lang:X4} open={open} mode=0x{mode:X}" +
                 (open < 0 ? $" error={_lastError} os={Environment.OSVersion.Version}" : ""));
    }

    /// <summary>直前の問い合わせが失敗したときの Windows のエラー番号（5 = 管理者のアプリへの問い合わせを拒否、1460 = 時間切れ）。</summary>
    private static int _lastError;

    /// <summary>答えなかった IME のウィンドウと、次に問い合わせてよい時刻（答えないところに毎回待たされてタスクバーが固まらないように）。</summary>
    private static readonly Dictionary<IntPtr, DateTime> SilentUntil = new();

    private static int Query(IntPtr ime, int command)
    {
        if (SilentUntil.TryGetValue(ime, out var until) && DateTime.Now < until) return -1;
        // 応答しないアプリで止まらないよう、短い時間で打ち切る
        if (SendMessageTimeout(ime, WM_IME_CONTROL, (IntPtr)command, IntPtr.Zero, 0x2 /*SMTO_ABORTIFHUNG*/, 300, out var r) == IntPtr.Zero)
        {
            _lastError = Marshal.GetLastWin32Error();
            if (SilentUntil.Count > 100) SilentUntil.Clear();
            SilentUntil[ime] = DateTime.Now.AddSeconds(10);
            return -1;
        }
        SilentUntil.Remove(ime);
        return (int)(long)r;
    }

    private static void Send(IntPtr ime, int command, int value) =>
        SendMessageTimeout(ime, WM_IME_CONTROL, (IntPtr)command, (IntPtr)value, 0x2, 100, out _);

    /// <summary>前面のアプリの IME の今の状態を読む。</summary>
    public static ImeState Read()
    {
        var (ime, lang) = ForegroundIme();
        if (lang != 0 && lang != 0x0411)
        {
            // 日本語以外のキーボード: 言語の略号（EN など）
            if (!LangCode.TryGetValue(lang, out var code))
            {
                try { code = new CultureInfo(lang).TwoLetterISOLanguageName.ToUpperInvariant(); } catch { code = "--"; }
                LangCode[lang] = code; // 毎秒作り直さない
            }
            return new ImeState(code, false, false, ImeMode.Direct, ime);
        }
        int open = ime != IntPtr.Zero ? Query(ime, IMC_GETOPENSTATUS) : -1;
        int mode = open > 0 ? Query(ime, IMC_GETCONVERSIONMODE) : 0;
        LogTarget(ime, lang, open, mode);
        bool usable = open >= 0;
        if (open < 0)
        {
            // 問い合わせに答えないアプリ（Windows Terminal・管理者として動くアプリなど）は、キー操作から推測した状態を出す
            open = InferredOpen.TryGetValue(Win32.GetForegroundWindow(), out var o) && o ? 1 : 0;
            mode = open > 0 ? NATIVE | FULLSHAPE | ROMAN : 0;
        }
        if (mode < 0) mode = 0;
        bool full = (mode & FULLSHAPE) != 0, native = (mode & NATIVE) != 0, kata = (mode & KATAKANA) != 0;
        var m = open == 0 ? ImeMode.Direct
            : native ? (kata ? (full ? ImeMode.Katakana : ImeMode.HalfKatakana) : ImeMode.Hiragana)
            : full ? ImeMode.FullAlpha : ImeMode.HalfAlpha;
        string text = m switch
        {
            ImeMode.Hiragana => "あ", ImeMode.Katakana => "カ", ImeMode.HalfKatakana => "ｶ", ImeMode.FullAlpha => "Ａ", _ => "A",
        };
        return new ImeState(text, true, usable && ime != IntPtr.Zero, m, ime);
    }

    /// <summary>
    /// 日本語入力を切り替えるキーが押された。前面のアプリが IME の問い合わせに答えるなら何もしない（実際の状態を読む）。
    /// 答えないアプリなら、キーに合わせてオン/オフを推測して覚える。マウスでの切り替えなどはわからないので、ずれることがある。
    /// </summary>
    public static void ToggleKeyPressed(uint vk)
    {
        var (ime, lang) = ForegroundIme();
        if (lang != 0 && lang != 0x0411) return;
        if (ime != IntPtr.Zero && Query(ime, IMC_GETOPENSTATUS) >= 0) return; // 読めるアプリは推測しない
        var fg = Win32.GetForegroundWindow();
        InferredOpen.TryGetValue(fg, out var open);
        InferredOpen[fg] = vk switch { 0x16 => true, 0x1A => false, _ => !open };
        if (InferredOpen.Count > 200) InferredOpen.Clear(); // 閉じたウィンドウの分が溜まらないように
    }

    /// <summary>日本語入力のオン/オフを切り替える（バーの表示のクリック）。</summary>
    public static void Toggle()
    {
        var (ime, lang) = ForegroundIme();
        if (lang != 0 && lang != 0x0411) return;
        int open = ime != IntPtr.Zero ? Query(ime, IMC_GETOPENSTATUS) : -1;
        if (open >= 0)
        {
            Send(ime, IMC_SETOPENSTATUS, open > 0 ? 0 : 1);
            return;
        }
        // 問い合わせに答えないアプリ（Windows Terminal など）には、日本語入力の切り替えキー（漢字キー）を送って切り替え、
        // 推測している状態も合わせて切り替える（自分で送ったキーはキーボード監視で数えないため）
        Input.Send((0x19, false), (0x19, true));
        var fg = Win32.GetForegroundWindow();
        InferredOpen.TryGetValue(fg, out var o);
        InferredOpen[fg] = !o;
    }

    /// <summary>入力モードを変える。ローマ字/かなの別は今のまま。<paramref name="ime"/> はポップアップを開いた時点の IME（途中で前面が変わっても対象を間違えない）。</summary>
    public static void SetMode(IntPtr ime, ImeMode mode)
    {
        if (ime == IntPtr.Zero) return;
        if (mode == ImeMode.Direct) Send(ime, IMC_SETOPENSTATUS, 0);
        else
        {
            int roman = Query(ime, IMC_GETOPENSTATUS) > 0 ? Query(ime, IMC_GETCONVERSIONMODE) & ROMAN : ROMAN;
            Send(ime, IMC_SETOPENSTATUS, 1);
            Send(ime, IMC_SETCONVERSIONMODE, ModeBits(mode) | roman);
        }
        NotifyMayHaveChanged();
    }

    private static int ModeBits(ImeMode m) => m switch
    {
        ImeMode.Hiragana => NATIVE | FULLSHAPE,
        ImeMode.Katakana => NATIVE | KATAKANA | FULLSHAPE,
        ImeMode.HalfKatakana => NATIVE | KATAKANA,
        ImeMode.FullAlpha => FULLSHAPE,
        _ => 0, // 半角英数
    };
}
