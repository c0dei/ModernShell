using System;
using System.Globalization;

namespace ModernShell.Core;

/// <summary>
/// 表示する文字の言語（日本語 / 英語）。コードには日本語の原文を書き、英語のときは辞書（LocStrings.cs）で置き換える。
/// 画面を作るたびに呼ぶので、言語を切り替えたあとに作り直した画面は新しい言語になる。
/// </summary>
public static partial class L
{
    /// <summary>英語で表示するか。設定が「自動」なら Windows の表示言語が日本語以外のとき英語。</summary>
    public static bool IsEnglish
    {
        get
        {
            var lang = Settings.Current?.Language ?? "";
            if (lang == "en") return true;
            if (lang == "ja") return false;
            return !CultureInfo.CurrentUICulture.Name.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>日本語の原文を、今の言語の文字にする（英語の訳が無ければ原文のまま）。</summary>
    public static string T(string ja) => IsEnglish && En.TryGetValue(ja, out var en) ? en : ja;

    /// <summary>{0} などを含む原文を訳してから埋め込む。</summary>
    public static string F(string ja, params object?[] args) => string.Format(T(ja), args);

    /// <summary>今の言語の書式（日付・数値）。</summary>
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(IsEnglish ? "en-US" : "ja-JP");
}
