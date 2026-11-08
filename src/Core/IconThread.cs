using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace ModernShell.Core;

/// <summary>
/// シェル（アプリ一覧・ファイル）のアイコンを取り出す専用のスレッド（STA）。
/// シェルのアイコン取得は COM で動く。普通のバックグラウンド スレッド（スレッド プール = MTA）から呼ぶと、
/// 実際の処理がアプリの最初の STA（= タスクバーの UI スレッド）へ回されることがあり、
/// 「別スレッドで読んでいるのにタスクバーが固まる」状態になる。専用の STA スレッドで 1 つずつ処理すれば、
/// COM の処理はこのスレッドで完結し、UI スレッドを止めない。
/// </summary>
public static class IconThread
{
    private static readonly BlockingCollection<Action> Queue = new();

    static IconThread()
    {
        var t = new Thread(() =>
        {
            foreach (var work in Queue.GetConsumingEnumerable())
            {
                try { work(); } catch (Exception ex) { Log.Error("icon thread", ex); }
            }
        }) { IsBackground = true, Name = "Icons" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>任意のアイコンの読み込みを専用スレッドで行う。</summary>
    public static Task<ImageSource?> Run(Func<ImageSource?> load)
    {
        var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() => tcs.SetResult(load()));
        return tcs.Task;
    }

    /// <summary>parsingName（"shell:AppsFolder\…" やファイル）のアイコンを専用スレッドで読む。</summary>
    public static Task<ImageSource?> Get(string parsingName, int size)
    {
        var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() => tcs.SetResult(IconHelper.Get(parsingName, size)));
        return tcs.Task;
    }
}
