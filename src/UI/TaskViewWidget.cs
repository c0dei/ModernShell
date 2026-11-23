using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ModernShell.Core;

namespace ModernShell.UI;

/// <summary>タスク ビューのウィジェットの絵（バーと設定画面のプレビューで共有）。</summary>
internal static class TaskViewLook
{
    private static readonly Brush ChipFill = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80));

    /// <summary>インジケータ（今のデスクトップの位置）を描く。count 個のうち current 番目（0 始まり）が現在位置。</summary>
    public static FrameworkElement Indicator(Theme t, TaskViewStyle style, int count, int current, bool vertical, double tileH)
    {
        var panel = new StackPanel { Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        if (style == TaskViewStyle.Icon) { panel.Children.Add(UiKit.Glyph("", vertical ? 16 : 17, t.Text)); return panel; }

        count = Math.Clamp(count, 1, 12);
        for (int i = 0; i < count; i++)
        {
            bool cur = i == current;
            FrameworkElement el;
            double gap = style == TaskViewStyle.Dots ? 6 : 4;
            if (style == TaskViewStyle.Dots)
            {
                // 現在位置だけ長いピル（アクセント色）、ほかは小さな点
                double w = cur ? 18 : 6;
                el = new Border
                {
                    Width = vertical ? 6 : w, Height = vertical ? w : 6, CornerRadius = new CornerRadius(3),
                    Background = cur ? t.AccentBrush : t.TextSecondary, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                };
            }
            else
            {
                double side = vertical ? 20 : Math.Max(20, Math.Min(26, tileH - 14));
                var text = UiKit.Label((i + 1).ToString(), 12, cur ? ImeLook.OnAccent(t) : t.Text, FontWeights.SemiBold);
                text.TextTrimming = TextTrimming.None;
                text.HorizontalAlignment = HorizontalAlignment.Center;
                text.VerticalAlignment = VerticalAlignment.Center;
                el = new Border { Width = side, Height = side, CornerRadius = t.Corner(6), Background = cur ? t.AccentBrush : ChipFill, Child = text };
            }
            if (i > 0) el.Margin = vertical ? new Thickness(0, gap, 0, 0) : new Thickness(gap, 0, 0, 0);
            panel.Children.Add(el);
        }
        return panel;
    }

    public static string StyleName(TaskViewStyle s) => s switch
    {
        TaskViewStyle.Dots => L.T("ドット（現在位置が長いピル）"),
        TaskViewStyle.Chips => L.T("番号チップ"),
        _ => L.T("アイコンのみ"),
    };
}

/// <summary>
/// タスク ビューのウィジェット: アイコンのみ / ドット / 番号チップで、今の仮想デスクトップの位置を表示する。
/// クリックでタスク ビュー（Win+Tab）、カーソルを置くと仮想デスクトップの一覧（タスク ビュー ボタンと同じ）。
/// </summary>
public sealed class TaskViewBarWidget : BarWidget
{
    public override string Id => "taskview";
    private Border? _host;
    private string _sig = "";

    protected override FrameworkElement BuildContent()
    {
        _host = new Border { VerticalAlignment = VerticalAlignment.Stretch };
        Refresh(true);
        PadOverride = new Thickness(0);
        return new Border { Background = T.Hover, BorderBrush = T.Stroke, BorderThickness = new Thickness(1), CornerRadius = T.Corner(6), Child = _host };
    }

    public override void Tick(int count) => Refresh(false);

    private void Refresh(bool force)
    {
        if (_host == null) return;
        var style = (Settings.Current ?? new Settings()).TaskViewWidgetStyle;
        var list = VirtualDesktops.List();
        int current = Math.Max(0, list.FindIndex(d => d.IsCurrent));
        string sig = $"{style}|{list.Count}|{current}";
        if (!force && sig == _sig) return;
        _sig = sig;
        var inner = TaskViewLook.Indicator(T, style, list.Count, current, Vertical, TileH);
        inner.Margin = style == TaskViewStyle.Icon ? new Thickness(Vertical ? 0 : 10, Vertical ? 6 : 0, Vertical ? 0 : 10, Vertical ? 6 : 0)
            : new Thickness(Vertical ? 0 : 10, Vertical ? 8 : 0, Vertical ? 0 : 10, Vertical ? 8 : 0);
        _host.Child = inner;
    }

    public override bool OnClick()
    {
        // 検索・タスク ビューのボタンと同じ: 管理者のアプリが前面でもキーが届くよう、先にバーを前面にする
        if (Card != null) TaskbarController.TakeForeground(Card);
        SystemActions.TaskView();
        return true;
    }

    public override IEnumerable<MenuItemSpec> ExtraMenu() => new[]
    {
        WidgetSettingsMenu.Choice("", L.T("表示の形"), (Settings.Current ?? new Settings()).TaskViewWidgetStyle,
            Enum.GetValues<TaskViewStyle>().Select(s => (s, TaskViewLook.StyleName(s))), (c, v) => c.TaskViewWidgetStyle = v),
        MenuItemSpec.Separator,
    };
}
