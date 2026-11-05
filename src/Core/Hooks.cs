using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using ModernShell.Native;

namespace ModernShell.Core;

internal static class Input
{
    /// <summary>自分が SendInput した入力を識別するタグ（フックで無視するため）。</summary>
    public static readonly IntPtr Tag = new(0x4D534831); // "MSH1"

    public static void Send(params (ushort vk, bool up)[] keys)
    {
        var inputs = new Win32.INPUT[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            inputs[i].type = Win32.INPUT_KEYBOARD;
            inputs[i].U.ki = new Win32.KEYBDINPUT
            {
                wVk = keys[i].vk,
                dwFlags = keys[i].up ? Win32.KEYEVENTF_KEYUP : 0,
                dwExtraInfo = Tag,
            };
        }
        Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
    }

    public static void Tap(ushort vk) => Send((vk, false), (vk, true));

    /// <summary>Win+X のようなショートカットを送る（フックはタグで素通りさせる）。</summary>
    public static void WinCombo(ushort vk) =>
        Send((Win32.VK_LWIN, false), (vk, false), (vk, true), (Win32.VK_LWIN, true));

    /// <summary>純正スタートメニューを開く（置き換え無効時のフォールバック）。</summary>
    public static void NativeStart() => Send((Win32.VK_LWIN, false), (Win32.VK_LWIN, true));
}

/// <summary>
/// 低レベルキーボード/マウスフック。
/// WH_*_LL はグローバルでも DLL 注入を伴わない（自プロセスのメッセージループで呼ばれる）ため explorer を不安定にしない。
/// コールバックは LowLevelHooksTimeout (~300ms〜1s) 以内に返す必要があり、超えると OS に黙って外される。
/// そのため重い処理はすべて Dispatcher.BeginInvoke で後回しにする。
/// </summary>
public sealed class InputHooks : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Win32.LowLevelProc _kbProc, _msProc;
    private IntPtr _kbHook, _msHook;

    private bool _winDown;
    private bool _comboUsed;
    private bool _swallowStartUp;

    /// <summary>Win キー単押し、または純正スタートボタンのクリック時に発火（UI スレッドで実行）。</summary>
    public event Action? StartRequested;

    /// <summary>ModernShell のメニューが開いているか。開いている間は Esc と外側クリックを監視する。</summary>
    public Func<bool> IsMenuOpen { get; set; } = () => false;
    /// <summary>メニュー表示中に画面のどこかでマウスボタンが押された（物理ピクセル座標）。UI スレッドで実行。</summary>
    public event Action<Win32.POINT>? MenuMouseDown;
    /// <summary>メニュー表示中に Esc が押された。UI スレッドで実行。</summary>
    public event Action? MenuEscape;

    /// <summary>ここが true の間だけ Win キーを横取りする。</summary>
    public Func<bool> InterceptWinKey { get; set; } = () => true;

    /// <summary>純正スタートボタンの矩形（物理ピクセル）。null なら横取りしない。別スレッドから更新される。</summary>
    public Win32.RECT? NativeStartButtonRect
    {
        get => _startRect?.Value;
        set => _startRect = value is { } r ? new Box(r) : null;
    }
    private volatile Box? _startRect;
    private sealed record Box(Win32.RECT Value);

    public InputHooks(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _kbProc = KeyboardProc;
        _msProc = MouseProc;
    }

    public void Install()
    {
        var hMod = Win32.GetModuleHandle(null);
        _kbHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _kbProc, hMod, 0);
        _msHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _msProc, hMod, 0);
        if (_kbHook == IntPtr.Zero || _msHook == IntPtr.Zero)
            Log.Info($"hook install failed: err={Marshal.GetLastWin32Error()}");
    }

    /// <summary>OS にフックを外された場合に備えて定期的に付け直す。</summary>
    public void Reinstall()
    {
        Uninstall();
        Install();
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Win32.CallNextHookEx(_kbHook, nCode, wParam, lParam);
        var k = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
        if (k.dwExtraInfo == Input.Tag) return Win32.CallNextHookEx(_kbHook, nCode, wParam, lParam);

        int msg = (int)wParam;
        bool down = msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
        bool up = msg is Win32.WM_KEYUP or Win32.WM_SYSKEYUP;

        // メニュー表示中の Esc はメニューを閉じるのに使い、前面のアプリには渡さない（Windows のメニューと同じ）
        if (k.vkCode == Win32.VK_ESCAPE && IsMenuOpen())
        {
            if (down) _dispatcher.BeginInvoke(() => MenuEscape?.Invoke());
            return (IntPtr)1;
        }
        bool isWin = k.vkCode is Win32.VK_LWIN or Win32.VK_RWIN;

        if (isWin && InterceptWinKey())
        {
            if (down)
            {
                if (!_winDown) { _winDown = true; _comboUsed = false; }
            }
            else if (up && _winDown)
            {
                _winDown = false;
                if (!_comboUsed)
                {
                    // Win の UP を握りつぶし、「マスク用ダミーキー → Win UP」を自分で送る。
                    // OS 側から見ると Win+(ダミー) の組み合わせになり、純正スタートは開かない。
                    Input.Send((Win32.VK_MASK, false), (Win32.VK_MASK, true), ((ushort)k.vkCode, true));
                    _dispatcher.BeginInvoke(() => StartRequested?.Invoke());
                    return (IntPtr)1;
                }
            }
        }
        else if (down && _winDown)
        {
            _comboUsed = true; // Win+E 等はそのまま通す
        }

        return Win32.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (_winDown && msg == Win32.WM_LBUTTONDOWN) _comboUsed = true;
            if (msg is Win32.WM_LBUTTONDOWN or 0x0204 /*RBUTTONDOWN*/ or 0x0207 /*MBUTTONDOWN*/ && IsMenuOpen())
            {
                var pt = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam).pt;
                _dispatcher.BeginInvoke(() => MenuMouseDown?.Invoke(pt));
            }

            if (msg is Win32.WM_LBUTTONDOWN or Win32.WM_LBUTTONUP && _startRect?.Value is { } r && InterceptWinKey())
            {
                var m = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
                if (m.dwExtraInfo != Input.Tag)
                {
                    if (msg == Win32.WM_LBUTTONDOWN && r.Contains(m.pt))
                    {
                        _swallowStartUp = true;
                        _dispatcher.BeginInvoke(() => StartRequested?.Invoke());
                        return (IntPtr)1;
                    }
                    if (msg == Win32.WM_LBUTTONUP && _swallowStartUp)
                    {
                        _swallowStartUp = false;
                        return (IntPtr)1;
                    }
                }
            }
        }
        return Win32.CallNextHookEx(_msHook, nCode, wParam, lParam);
    }

    private void Uninstall()
    {
        if (_kbHook != IntPtr.Zero) { Win32.UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
        if (_msHook != IntPtr.Zero) { Win32.UnhookWindowsHookEx(_msHook); _msHook = IntPtr.Zero; }
    }

    public void Dispose() => Uninstall();
}
