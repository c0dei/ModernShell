using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using ModernShell.Core;

namespace ModernShell.UI;

/// <summary>
/// IME の状態表示は IME のウィジェット（<see cref="ImeBarWidget"/>）が担う。ここは、キー操作・前面のアプリの切り替えを
/// ウィジェットへ伝える部分。読み取りと切り替えは <see cref="ImeControl"/>。
/// </summary>
public sealed partial class TaskbarController
{
    private DispatcherTimer? _imeSoon, _imeLater;

    /// <summary>日本語入力を切り替えるキーが押された（問い合わせに答えないアプリでは、キーから状態を推測する）。</summary>
    public void ImeToggleKey(uint vk)
    {
        if (_disposed) return;
        ImeControl.ToggleKeyPressed(vk);
        ImeControl.NotifyMayHaveChanged();
    }

    /// <summary>
    /// IME の状態が変わったかもしれないとき（キー操作・アプリの切り替え）に呼ぶ。IME はキーを受け取ってから少し遅れて
    /// 状態を変えるので、少し待って読み、念のためもう一度読む。
    /// </summary>
    public void ImeMayHaveChanged()
    {
        if (_disposed || ImeWidgetCard() == null) return;
        _imeSoon ??= new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Normal, (s, _) => { ((DispatcherTimer)s!).Stop(); ImeControl.NotifyMayHaveChanged(); }, _dispatcher);
        _imeLater ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (s, _) => { ((DispatcherTimer)s!).Stop(); ImeControl.NotifyMayHaveChanged(); }, _dispatcher);
        _imeSoon.Stop(); _imeSoon.Start();
        _imeLater.Stop(); _imeLater.Start();
    }

    /// <summary>IME のウィジェットのカード（バーに置いてあるとき）。</summary>
    private FrameworkElement? ImeWidgetCard() => _barWidgets.OfType<ImeBarWidget>().FirstOrDefault()?.Card;
}
