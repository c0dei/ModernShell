using System;
using System.Collections.Generic;
using System.Linq;
using ModernShell.Core;

namespace ModernShell.UI;

/// <summary>
/// ウィジェットの右クリックメニューから、そのウィジェットの設定を変える部品。
/// 選ぶとすぐ保存され、バーに反映される（設定画面を開かなくてよい）。
/// </summary>
internal static class WidgetSettingsMenu
{
    private const string Check = "";

    /// <summary>チェック付きのオン/オフ項目。</summary>
    public static MenuItemSpec Toggle(string title, bool on, Action<Settings, bool> set, bool enabled = true) =>
        new(on ? Check : "", title, () => Apply(s => set(s, !on))) { Enabled = enabled };

    /// <summary>選択肢から 1 つ選ぶ項目（開くと選択肢の一覧が出て、今の値にチェックが付く）。</summary>
    public static MenuItemSpec Choice<T>(string glyph, string title, T current, IEnumerable<(T Value, string Label)> options, Action<Settings, T> set, bool enabled = true) where T : notnull
    {
        var list = options.ToList();
        return new MenuItemSpec(glyph, title, () => { })
        {
            Enabled = enabled,
            Children = () => list.Select(o => new MenuItemSpec(EqualityComparer<T>.Default.Equals(o.Value, current) ? Check : "", o.Label, () => Apply(s => set(s, o.Value)))).ToList(),
        };
    }

    private static void Apply(Action<Settings> change)
    {
        if (Settings.Current is not { } s) return;
        change(s);
        s.Save();
    }
}
