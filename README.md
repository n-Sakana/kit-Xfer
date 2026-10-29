# Xfer

ファイルやフォルダを、画面とカメラの間で運ぶ光学転送アプリです。
送受信、スマホの PWA リモコン、表示（Tile / QR）と読取りの設定を備えています。

[decimen-optical-transfer](https://github.com/bashalarmistalt/decimen-optical-transfer)
v0.3.0 を基にしています。ライセンスは [第三者表示](THIRD-PARTY-NOTICES.md) を参照してください。

## Windows で使う

リポジトリ全体を配置し、`Xfer.vbs` をダブルクリックします。
画面は MiniUI で表示します。Windows PowerShell 5.1 が `runtime\windows-x64` の MiniUI を読み込み、
`ui\index.html` と、その横の `ui\MiniUI.App.dll`（Xfer の本体）を動かします。
通常終了時は事前ビルド済みの `runtime\windows-x64\Xfer.Launcher.dll` が本体の終了処理を呼び、診断ログの末尾を保存します。
起動・終了時にこの接続用DLLをC#コンパイルすることはありません。
.NET SDK、WebView2、ブラウザは不要です。
起動ログを表示する場合は `Xfer.cmd` を使います。
ログは `%LOCALAPPDATA%\Xfer\logs` に保存します。
ダウンロードした zip から展開した場合は、ランタイムの DLL を
`%LOCALAPPDATA%\Xfer\runtime` に書き写してから読み込みます。

`tools\windows\build.cmd` は Xfer の本体と終了処理用 DLL をビルドします。
.NET SDK 9 と Visual Studio の C++ ビルドツールが必要です。
MiniUI の DLL は `runtime\windows-x64` に同梱してあり、MiniUI の repo は不要です。

`install.bat` は管理者権限を使わず、HKCU のファイル・フォルダ・エクスプローラ背景・
デスクトップ背景に **Xfer** を登録します。ファイル／フォルダでは
`Xfer > Open` の1項目、背景では直接アプリを開きます。
Windows 11 では従来形式のメニュー（「その他のオプションを確認」）に表示されます。
`uninstall.bat` はこの4か所の登録を一覧表示し、選んだ登録をバックアップして削除します。
Xfer を削除する場合は、一覧で Xfer を選びます。
配布フォルダを移した場合は `install.bat` を再実行してください。
改名前の保存先・メニュー登録の自動移行は行いません。

## 起動オプション

```powershell
.\Xfer.cmd --path ".\input" --mode optical
.\Xfer.cmd --no-browser
```

Markdown にしたものを転送する場合は、File2MD の出力ファイルを選びます。
QR の既定値は 1000 bytes / 30 fps / 誤り訂正 L です。

`--port` で待受ポートを指定できます。

## 表示：Tile と QR

送る画面の「表示」で Tile（既定）と QR を切り替えます。設定欄は方式ごとに別で、切り替えると欄の中身だけが入れ替わります。
受け取る側は、どちらが映っても設定なしで読みます。
スマホの PWA は QR で送受信します。スマホのカメラでこの画面を読むときは、「表示」を QR にしてから出します。

Tile は窓いっぱいを白黒のセルで埋めます。48 セルごとに位置マーカー（黒い 3×3、白い枠、ID の環）を置き、
4 つのマーカーで囲まれたパッチ 1 つに Fountain フレーム 1 個を CRC-32 と Reed-Solomon 付きで載せます。
パッチは独立しているので、画面の一部しか映っていなくても、傾いていても、写った区画の分だけ進みます。
既定は 30 fps です。絵の切り替わりに重なった区画は捨て、読めた区画と後続の絵で復元します。
Tile の送信開始で同じモニターの全画面表示にし、操作欄も含めた画面全体にデータを描きます。
操作欄は下端へポインターを移すか Tab キーで表示できます。Esc で送信を止め、元の窓へ戻ります。
表示領域 1920×1080 CSS px・セル 3 px では 13×7=91 区画です。区画は整数個なので、操作欄を外してもこの条件の区画数は増えません。
受信の既定は撮影幅 1920・30 fps・読み取り 3 並列です。
位置マーカーは毎回 ID を確かめながら追跡し、区画の CRC が通らなくなったときや画面が動いたときは探し直します。

| 設定 | 意味 |
| --- | --- |
| セルの大きさ | 1 セルの大きさ（CSS px、既定 3）。画面にはそれに表示倍率を掛けた端末の画素の整数で描きます。カメラで 2 画素ほどまで読めます |
| 中央の粗い区画 | 既定はなし。2 倍・3 倍を選ぶと中央にセルの大きい区画（最大 2×2 パッチ）を置き、遠い・ピントが外れたカメラでも中央だけで進みます |
| 色を重ねる | 既定はオフ。2×2 マスに2ビットの色を追加。色には独立した Reed-Solomon（48バイト）と CRC を付け、両層の組合せも検算します。受信側は自動判別します |
| 誤り訂正 | L / M / Q / H。白黒部分の Reed-Solomon の量（既定 L） |
| 1 秒の枚数 | 既定 30 fps（QR は別に覚え、既定 30 fps・誤り訂正 L） |

形式と読み取りは `src/Optical/Fx2.cs` と `src/Optical/Fx2Reader.cs`、カメラなしの試験は `tests/Fx2Lab.cs` です。
設定や起動引数・送る符号の中では、Tile を `fx2` と呼びます（`--layout fx2`）。
Xfer の保存先にある設定はそのまま使います。新しい既定値を使う場合は Tile のセルを 3 px、枚数を 30 fps、誤り訂正を L、色をオフ、受信の並列数を 3 にします。
2026-10-29 の C920・EV2450 の同一PDF測定では、全画面・白黒・3 px・30 fps・L が候補中最速の10.372秒でした。
ピント保持の判定を調整した後、同じ設定を既定として起動し、9.760秒・SHA-256一致で完了しました（初回の有効フレームから保存完了まで。単発の実機測定）。
全画面＋色・3 px・10 fps・H は45.597秒で検算一致まで完了しましたが、色の読取り失敗が多く、既定には採用していません。
同じカメラで10.48秒を半分にする目標は未達です。色を試す場合の確認済み条件は10 fps・Hです。

## 測定用の起動

窓を出してすぐ送る・読むための起動です。指定はその窓だけに効き、保存されません。

```bash
./Xfer --port 18431 --path ./資料.pdf --send --layout fx2 --cell 6 --coarse 2 --fps 15 --ec M
./Xfer --port 18431 --path ./資料.pdf --send --layout qr --fps 30 --frame-bytes 1000 --ec L --qr-size 900
```

```powershell
.\Xfer.cmd --port 18432 --receive --camera C920 --camera-width 1920 --camera-fps 30 --workers 3 --output .\受信
```

`--keystone-x` / `--keystone-y`（%）は絵を台形にゆがめて出し、カメラを動かさずに傾きへの強さを測ります。
Linux の窓は `--window-at X,Y`（X11 の画素、xrandr や wmctrl と同じ単位）、`--window WxH`、`--fullscreen` で置けます。
`--window-at` と `--fullscreen` を一緒に使うと、窓が指定の位置に来たのを確かめてから全画面にし、5 秒たっても来なければ全画面にしません。
Tile の色は `--color` / `--mono` でその起動だけ指定できます。QR とスマホPWAの形式は変えていません。

## Linux・開発

```bash
dotnet build Xfer.csproj
dotnet run --project Xfer.csproj -- --no-browser
```

.NET SDK 9 を使います。HTTP 画面は表示された localhost URL から開きます。
Linux の起動コマンドは `./Xfer` です。Windows と同じ `ui/` の画面を MiniUI で開きます。
MiniUI の DLL は `runtime/linux-x64` に同梱してあり、MiniUI の repo は不要です。
詳しくは [Linux 手順](tools/linux/README.md) を参照してください。

MiniUI を更新するときだけ、選んだ版から作った部品を Xfer の `runtime/` で入れ替えます。
通常の起動・ビルドでは外部の MiniUI を読みません。[部品と更新手順](runtime/README.md)を参照してください。

スマホの PWA リモコンには、Tailscale の HTTPS 接続を利用できます。

[テスト手順](tests/README.md)

## 通常操作の診断ログ

起動、操作、送信準備・圧縮・QR生成、表示更新、撮影・QR復号、中継、復元・検算・保存、
中止・終了・失敗を、自動的にJSONL（1行1イベント）へ記録します。UIの追加や転送方式の変更はありません。

- Windows: `%LOCALAPPDATA%\Xfer\logs` の `trace-*.jsonl` と `launcher.jsonl` / `launcher.jsonl.previous`。
- Linux: 通常は `~/.local/share/Xfer/logs/trace-*.jsonl`（.NETのLocalApplicationData配下）。
- 試験用の保存先指定: 起動前に環境変数 `Xfer_LOG_DIR` を設定します。
- ブラウザ／スマホPWA: **開いたURLのoriginごと、端末ごと**のIndexedDB `Xfer-diagnostics`、
  `events` と停止・障害前後を保護する `incidents` ストア。PCへ通信できなくても端末内の保存は続きます。

PWAのログは、ページが開いて通信できる間、既存のPC接続先へ自動回収します。PCの同じログフォルダに
`browser-trace-*.jsonl`、保護履歴を `browser-incident-*.jsonl` として保存します。
**PC側のログフォルダをまとめてコピー**すれば回収できます。スマホへのコマンド入力は不要です。
通信断中の履歴は接続回復後、または同じURLのページを再度開いた後に順次回収します。
PCが書き終えた確認を受けるまで端末側の送信済み位置を進めません。確認応答の消失や複数タブでは同じ行が
重複する場合があるため、解析時は `run` と `seq` の組でまとめてください。
ブラウザデータを消すと端末内ログも消えます。別originの履歴を回収するにはそのURLのページを開きます。
開発用の補助として `XferDiagnostics.download()` / `exportJsonl()` も残しています。

各行には `schema: 1`、UTC時刻 `utc`、プロセス／ページ内の経過時間 `monoMs`、実行ID `run`、
連番 `seq`、`route`、`event`、照合キー `correlation`、選択されたメタデータ `data` があります。
起動行とheartbeatで端末の仮名ID、OS・ブラウザ／ランタイム、実行版を確認できます。ホストの `build` はassemblyのMVID、
ブラウザの `build` は配信アセットの識別子です。端末の実名、カメラの実名、ファイル名・パス・内容、
QR生データ、撮影画像、認証トークン、例外の本文や全スタックは保存しません。
エラーは段階・型・コード・製品内の位置で記録します。C#では呼出元のファイル名／メソッド／行、取得可能なら原因の製品内1フレームも記録。
workerではimport／WASM初期化／復号の各段階とWASM取得のHTTP状態、launcherでは依存確認／ロード／起動などを区別します。

送受信を照合するキーは既存QRヘッダーの `session:blocks:blockLength:totalLength`。
`send.ready` と `receive.session`、PWAの `relay.accepted` などで一致します。QRの形式は変えていません。
セッション番号は既存の16 bitなので、世界全体で一意なIDではありません。UTC・実行ID・条件も合わせて照合します。
`receive.session.data.receiver` とPWAの受信イベントの `correlation` も照合できます。
PCの `http.*.data.last.client` はPWAの `page.start.correlation` に対応します。

停止・遅さを見るときは、次の段階を分けて確認してください。

| 記録 | 意味 |
| --- | --- |
| `send.prepare/bundle/compress/hash.*`, `send.qr` | 読出し・梱包・圧縮・QR生成。表示成功とは別 |
| `send.ui_tick`, `send.image_request`, `send.image_cache_miss` | MiniUIの表示要求と画像取得。画面の物理的な発光を測った値ではない |
| `send.draw`, `send.queue_empty`, `send.status` | ブラウザの描画、先読み不足、待機数 |
| `camera.capture`, `camera.video_callback`, `camera.no_worker` | 画像到着／切出しとworkerの空き。`camera.status` に最終到着・読取りからの時間 |
| `camera.qr_read` / `decode.read` | QRの復号成功。採用・復元進行とは別 |
| `decode.foreign`, `decode.non_transfer` | 別セッションや転送用でないQRをworkerが観測。複数探索の観測数であり、固有QR数ではない |
| `relay.accepted/duplicate/full/retry`, `http.*` | PWAキューへの採用・重複・満杯・既存再試行・通信時間。PCでの採用とは別 |
| `receive.accepted/duplicate/reject.*` | PCでの新規フレーム採用・重複・形式／セッション／停止後の拒否 |
| `receive.*.data.last.solved/blocks/progressAgeMs` | Fountainで解けたブロック数と、その数が最後に増えてからの時間。読取り回数とは別 |
| `receive.assemble/verify/decompress/save.*`, `receive.complete` | 復元・整合性確認・保存と完了。`*.start`の後に`*.ok`がなければその段階を確認 |
| `*.failed`, `*.stop`, `*.stopped`, `*.expired`, `process.exit`, `page.pagehide` | 失敗・明示的停止・期限切れ・終了 |

頻繁な処理は5秒ごとに `count`、`totalMs`、`maxMs` と最後の状態 `last` を出力します。
平均処理時間は `totalMs/count`。`last` は区間最後の値で、積算値ではありません。
ページのheartbeatには通信待ちの段階・経過時間、可視性、online状態、タイマーの実間隔を残します。
ホストのheartbeatが続いて撮影集計が増えない場合と、読取りは増えるが `solved` が増えない場合を区別できます。
Windows通常終了は `native.shutdown.start` → `app.stop` → `native.shutdown.end` と、launcher側の
`launcher.native_shutdown` を確認してください。Native AOT DLLの終了はホストの `ProcessExit` に頼らず明示的に処理します。
`launcher.native_shutdown.data.code` は0=後始末とflush完了、1=2秒上限、2=後始末例外、3=保存失敗、
-1=本体未ロード、-2=終了関数なし、-3=接続用DLLなし。上限や失敗時でもウィンドウの終了を妨げません。
時刻は端末時計に依存するため、端末間のミリ秒単位の比較には時計のずれも考慮してください。

ホストはバックグラウンド書込み、待ち行列256件・集計キー256個まで、約2 MiB×8ファイルで古いログを回転します。
PowerShell起動ログは約2 MiB×2ファイル。旧版の `Xfer_*.log` は自動削除しません。
PWAは非同期IndexedDB書込みで最新16,384件（origin全体）を保持します。
停止・失敗・offline時には直前最大512件を別枠へコピーし、その別枠は合計4,096件まで保持します。
待機中のheartbeatで保護履歴は押し出されません。後続の停止・障害が増えると古い保護履歴から回転します。
既存の実HTTP＋合成カメラ試験ログは142件／22.375秒で、同じ頻度なら旧512件は約1.34分、16,384件は約43分です。
実スマホでの保持時間を保証する値ではありません。メモリ待ち行列128件・集計128キー・通信待ち32件までです。
自動回収は10秒ごとに最大4バッチ、各48件／約48 KB、同時通信1本・4秒上限。転送本体の再試行条件は変えません。
PCは既存のloopback／Tailscale接続・認証境界を使用し、同origin・専用ヘッダー・JSON・64 KiB上限・深さ／項目検査を行います。
回収の書込みは同時1本・全体120バッチ／分まで。通常回収は2 MiB×8ファイル、保護履歴は2 MiB×4ファイルで別々に回転します。
過負荷による破棄や保存失敗は `logging.loss` に記録します（保存先が復旧するまでファイルには出せません）。
ストレージ拒否時のPWAは最新128件をメモリだけに保持し、開いている間は自動回収とexportができます。
強制終了、停電、ブラウザの凍結・終了では直近約5秒や終了行が残らない場合があります。
終了行がないことだけで、転送失敗や特定の停止原因を断定しないでください。
