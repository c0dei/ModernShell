# ModernShell

Start11 のような **モダンなスタートメニュー / タスクバー置き換え** を、
**explorer.exe に一切コードを注入しない** 方式で実現する Windows 11 / 10 向けアプリです。

> ExplorerPatcher / Windhawk はどちらも explorer.exe のプロセス内にコードを入れて（DLL 注入・関数フック）
> 純正 UI を書き換えます。Windows Update で内部実装が変わると、それが explorer のクラッシュに直結します。
> ModernShell は **別プロセスで自前の UI を描画し、公開 API だけで純正 UI を「横取り・非表示」にする** ため、
> ModernShell が落ちても explorer は落ちません。

## 機能 (v0.2)

### スタートメニュー
- Win キー単押し / 純正スタートボタンのクリックを横取りして独自スタートを表示（Win+E などの組み合わせはそのまま）
- Acrylic / Mica 背景、角丸、ライト/ダーク自動追従、アクセントカラー
- ピン留め（右クリックでピン留め/解除、ドラッグ＆ドロップで並べ替え、先頭へ移動）
- すべてのアプリ（A–Z / 五十音の行でグループ化。ストアアプリ含む `shell:AppsFolder` を列挙）
- インクリメンタル検索（前方一致・単語先頭・部分一致・頭文字、ひらがな/カタカナ・全角/半角を同一視）
  ↑↓ で選択、Enter で起動、Ctrl+Shift+Enter で管理者として起動、コマンド実行 / Web 検索
- 管理者として実行、ファイルの場所を開く
- 電源メニュー（ロック / スリープ / サインアウト / 再起動 / シャットダウン / ModernShell の終了）
- **ウィジェット**（右側の列。個別にオン/オフ）: 時計とカレンダー、天気（Open-Meteo）、システム（CPU/メモリ/ディスク）、
  フォルダー、最近使ったファイル、メモ
- 背景効果: Windows 11 22H2 以降は公式の Mica/Acrylic、それ以外や透けない場合は Acrylic ぼかし（設定で切替）

### タスクバー（任意・既定 OFF）
- 純正タスクバーを隠し、AppBar として独自タスクバーを表示（作業領域も正しく確保）
- **スタイル × レイアウトの 4 通り**

  | | まとめる（スタート＋アプリ） | 3 分割（左・中央・右） |
  |---|---|---|
  | **帯** | 従来の Win11 風 | 左=スタート / 中央=アプリ / 右=システム・時計 |
  | **アイランド** | Mac のドック風（中身に合わせて伸縮）＋右に時計の島 | 3 つの独立した島 |

  アイランドは島ごとに別ウィンドウなので、島それぞれに Acrylic と角丸が付き、島の間のデスクトップはそのままクリックできる。
  ホバー時のアイコン拡大（オフ可）、画面端からの余白を調整可能
- スタート・検索・タスクビューの位置を「アプリと一緒（中央/左）」か「左端」から選択、検索・タスクビューは個別に非表示可
- **通知領域（トレイアイコン）**: アプリのトレイアイコンを表示。「^」で隠れたアイコン、そこから常時表示するアイコンを選択
- クリックで最小化 ⇔ 復元（シェルフックで正確にアクティブ状態を追跡）、点滅（通知）表示
- 中央 / 左寄せ、上 / 下、高さ変更
- システムアイコン: ネットワーク接続状態、バッテリー残量（ノート PC のみ・充電中表示）
- ピン留め＋実行中ウィンドウ、同一アプリのグループ化（複数ウィンドウは一覧ポップアップ）
- クリックで前面化 / 最小化、中クリックで新規起動、右クリックでピン留め・閉じる
- 全画面アプリ中は最前面を解除
- 時計（クリックで通知センター）、クイック設定ボタン、デスクトップの表示

### 安全装置
- **Ctrl + Alt + Shift + F12** でいつでも終了して純正タスクバーに戻る
- **ウォッチドッグ**: 本体が強制終了・クラッシュしても、見張り役のプロセスが純正タスクバーとトレイアイコンを戻す
- 終了時・例外時・次回起動時に純正タスクバーを自動で元に戻す
- 万一隠れたままになったら `ModernShell.exe --restore`
- explorer が再起動しても (`TaskbarCreated`) 状態を再適用
- スリープ復帰 / ロック解除時にキーボードフックを付け直す

## ビルド

Windows + .NET 8 SDK:

```bash
dotnet build ModernShell.csproj -c Release
```

単一 exe にする場合:

```bash
dotnet publish ModernShell.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

### GitHub Actions（自動ビルド / リリース）

- `main` への push・PR ごとに windows-latest でビルドし、Actions の Artifacts に exe を置きます。
- `v` から始まるタグを push すると、**Releases にランタイム込みの単一 exe（x64 / ARM64）を自動で添付**します。

```bash
git tag v0.1.0
```

```bash
git push origin v0.1.0
```

exe は .NET ランタイム同梱なので、インストール不要でそのまま起動できます。

設定ファイル: `%AppData%\ModernShell\settings.json` / ログ: `%AppData%\ModernShell\modernshell.log`

## アーキテクチャ

```
src/
  App.cs                  エントリポイント、単一インスタンス、各部品の配線、例外/復元処理
  Native/Win32.cs         P/Invoke 定義
  Core/
    Hooks.cs              WH_KEYBOARD_LL / WH_MOUSE_LL（Win キー・スタートボタン横取り）
    NativeTaskbar.cs      純正タスクバーの非表示/復元、UIA でスタートボタン位置取得
    AppCatalog.cs         shell:AppsFolder 列挙（デスクトップ + ストアアプリ）、起動
    ShellHost.cs          ManagedShell（トレイ・タスク一覧・純正タスクバー非表示）の起動と後始末
    WidgetData.cs         ウィジェット用データ（天気・CPU/メモリ・フォルダー・最近使ったファイル・メモ）
    WindowHelpers.cs      前面化・ピン留めと実行中ウィンドウの照合
    IconHelper.cs         IShellItemImageFactory でアルファ付き高解像度アイコン
    SystemActions.cs      電源操作、Win+A/N/D 等の送出、スタートアップ登録
    Settings.cs           JSON 設定、ログ
  UI/
    StartMenuWindow.cs    スタートメニュー
    TaskbarController.cs  タスクバー全体（レイアウト組み立て・アプリボタン・時計/システムアイコン）
    AppBarReservation.cs  作業領域の予約だけを行う不可視 AppBar
    BarSurface.cs         見た目 1 枚分（帯なら 1 枚、アイランドなら島ごとに 1 枚）
    SettingsWindow.cs     設定画面（Fluent デザイン、トグルスイッチ）
    WidgetPanel.cs        スタートメニューのウィジェット列
    Theme.cs              配色・Mica/Acrylic 適用
    Controls.cs           Tile（フラットボタン）、Flyout（モダンなコンテキストメニュー）
verify/Verify.csproj      Linux 上でのコンパイル検証用（Windows では不要）
```

### 「注入しない」ための技術選択

| やりたいこと | ModernShell の方法 | 注入型ツールの方法 |
|---|---|---|
| Win キーで独自スタート | 低レベルキーボードフック + マスクキー送出（AutoHotkey と同じ手法） | explorer 内の関数をフック |
| 純正スタートボタンのクリック | UIA で位置取得 + 低レベルマウスフック | XAML ツリー改変 |
| タスクバー置き換え | 純正を `ShowWindow(SW_HIDE)` + 自前 AppBar | explorer 内の描画を改変 |
| ウィンドウ一覧 | シェルフック（ManagedShell の TasksService） | explorer 内部状態を参照 |
| トレイアイコン | 自前の `Shell_TrayWnd` でアイコン登録を受け取る（ManagedShell の TrayService、RetroBar と同じ） | explorer 内の通知領域を改変 |

低レベルフック (`WH_*_LL`) は DLL 注入を伴わず、自プロセスのメッセージループで呼ばれます。
コールバックは軽量にして重い処理は Dispatcher に回しています（遅いと OS にフックを外されるため）。

## 既知の制限 / ロードマップ

- [x] 通知領域（トレイアイコン）— v0.2 で ManagedShell により対応
- [ ] トレイアイコンのドラッグ並べ替え、通知バルーンの表示
- [ ] 音量アイコンの実際の音量・ミュート反映（Core Audio）
- [ ] アイランドの自動非表示（作業領域を確保しないモード）
- [ ] マルチモニター（現在はプライマリのみ）
- [ ] ストアアプリのウィンドウ（ApplicationFrameHost）の AUMID 判定精度向上
- [ ] ジャンプリスト / 最近使ったファイル（おすすめ）
- [ ] タスクバーのドラッグ並べ替え、ホバー時のサムネイルプレビュー（`DwmRegisterThumbnail`）
- [ ] スタートのフォルダ（グループ）、レイアウト切替（Win7 風 / Win10 風）
- [ ] すべてのアプリの頭文字ジャンプ、仮想化による高速化
- [ ] 検索: ファイル検索（Windows Search の OLE DB）、設定項目検索
- [ ] インストーラ、自動更新

## 動作確認のお願い（未検証ポイント）

このコードは Linux 上で Windows 用参照アセンブリに対してコンパイル確認済み（エラー/警告 0）ですが、
**実機 Windows での実行確認はまだ**です。特に以下は環境差が出やすいので確認してください:

1. Win キー単押しで純正スタートが開かず、ModernShell のスタートだけが開くか
2. 純正タスクバーのスタートボタンのクリック横取り（UIA で位置が取れているか）
3. Acrylic / Mica 背景が透過して見えるか（真っ黒になる場合は設定で「Solid」に）
4. 独自タスクバー ON/OFF 切替で作業領域が正しく戻るか
5. 高 DPI（125%/150%）での位置・サイズ
6. アイランド型で島の角丸・Acrylic が島ごとに正しく描かれるか、島の幅がアプリの増減に追従するか

問題があれば `%AppData%\ModernShell\modernshell.log` を確認してください。

## ライセンス表記

通知領域とタスク一覧に [ManagedShell](https://github.com/cairoshell/ManagedShell)（Apache-2.0）を使用しています。詳細は [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
