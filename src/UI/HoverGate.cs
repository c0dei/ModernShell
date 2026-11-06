using System;
using System.Windows;
using System.Windows.Threading;

namespace ModernShell.UI;

/// <summary>
/// カーソルを置いてから少し待って開き、離れたら少し待って閉じる、ホバー型ポップアップの開閉タイミング。
/// ポップアップの上にカーソルが移ったときは閉じない。ウィンドウのプレビューとデスクトップ一覧で共通。
/// </summary>
public sealed class HoverGate
{
    private readonly DispatcherTimer _show, _hide;
    private Action? _pending;

    /// <param name="close">閉じるときに呼ぶ処理。</param>
    public HoverGate(Dispatcher dispatcher, Action close, int hideDelayMs = 250)
    {
        _show = new DispatcherTimer(DispatcherPriority.Normal, dispatcher);
        _show.Tick += (_, _) => { _show.Stop(); var p = _pending; _pending = null; p?.Invoke(); };
        _hide = new DispatcherTimer(TimeSpan.FromMilliseconds(hideDelayMs), DispatcherPriority.Normal, (_, _) => { _hide!.Stop(); close(); }, dispatcher);
        _hide.Stop();
    }

    /// <summary>delayMs 後に show を実行する（その間に離れたら取り消し）。</summary>
    public void ShowAfter(int delayMs, Action show)
    {
        _hide.Stop();
        _pending = show;
        _show.Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMs));
        _show.Stop();
        _show.Start();
    }

    /// <summary>閉じる予定を取り消す（ボタンやポップアップの上にカーソルが戻った）。</summary>
    public void KeepOpen() => _hide.Stop();

    /// <summary>カーソルが離れた。開く予定は取り消し、開いていれば少し後に閉じる。</summary>
    public void Leave(bool isOpen)
    {
        _show.Stop();
        _pending = null;
        if (isOpen) { _hide.Stop(); _hide.Start(); }
    }

    /// <summary>予定をすべて取り消す（すぐ閉じるとき）。</summary>
    public void Stop()
    {
        _show.Stop();
        _hide.Stop();
        _pending = null;
    }

    /// <summary>ポップアップの中身に付ける: 上にカーソルがある間は閉じない。</summary>
    public void Attach(FrameworkElement popupContent)
    {
        popupContent.MouseEnter += (_, _) => KeepOpen();
        popupContent.MouseLeave += (_, _) => Leave(isOpen: true);
    }
}
