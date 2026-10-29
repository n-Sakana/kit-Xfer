# Xfer のテスト

C920 の実カメラ測定装置は [CameraLab.md](CameraLab.md) を参照してください。
解像度・fps・露出・ピント・ゲイン、読取率・新規/重複QR・マス幅・行差分・AF固定比較を記録します。

ピント固定の共通処理は、窓・カメラなしで確認できます。

```bash
dotnet run --project tests/Xfer.FocusHold.csproj -c Release --artifacts-path <出力先>
```

現在値での固定、再固定、AF復帰、手動設定・非対応機器の維持、設定失敗時の復帰、
終了時の再試行、複数workerと終了の競合を確認します。Windows実機はCameraLabの
`focus-hold OUT [CAMERA]` で別途確認します。LinuxのV4L2実機動作はこの共通試験には含みません。

## Tile

`Xfer.Fx2Lab.csproj` はカメラなしの Tile の試験です。製品の `Fx2` / `Fx2Reader` / Fountain をそのまま使います。

```bash
dotnet build tests/Xfer.Fx2Lab.csproj -c Release --artifacts-path <出力先>
dotnet <出力先>/bin/Xfer.Fx2Lab/release/Xfer.Fx2Lab.dll selftest
dotnet <...>/Xfer.Fx2Lab.dll sim --screen 1920x1050 --cell 3 --coarse 2 --scale 1.26 --blur 0.8 --noise 3 --frames 120
dotnet <...>/Xfer.Fx2Lab.dll pgm tests/results/fx2-20261021/S1 [--out <重ね描きの出力先>]
```

- `selftest`：Golay 符号（最小距離 8・3 ビット訂正）、ID 環の 8 方向、パッチの往復・訂正・拒否、
  全訂正レベルで既存の Reed-Solomon 符号との一致（各100パターン）、レイアウト、きれいな絵の全パッチ復号、
  追跡中の新しい内容の復号・画面移動後の再探索・画面消失後の拒否、QR の絵を Tile と取り違えないこと。
- `sim`：合成カメラ（射影・歪み・ぼけ・雑音・露光と両側のローリングシャッター・一部だけ写る）で乱数の内容を受け取り、
  一致と所要時間・1 コマの復号数を出します。
- `pgm`：CameraLab の `--save-all` などで保存した 8 bit PGM を読み、コマごとのマーカー・パッチ・段ごとの復号数・処理時間と、
  2 秒あたりの異なるパッチ数・セルのカメラ画素数を出します。`--out` は検出結果の重ね描き（PPM）。

`NativeLoopbackProbe`（Headless / MiniUI Probe）は QR に続いて Tile も、送り手の `Fx2Stream` / `Fx2Image` から
受け手の `CameraReceiver` を通し、受け取ったファイルが元と一致することを確かめます。
撮影プレビューの2分の1縮小は、奇数幅を含めて各色の平均値と完全一致することも確認します。

`receive_report.py` は診断ログ（trace-*.jsonl）から受信ごとに、所要時間、速さ（送った量 / 秒）、撮影・読取り数、
新規／重複フレーム、停止（5 秒区間で新規 5 以下が 10 秒以上続く）を出します。`--table` で 5 秒ごとの表も出します。

```bash
python tests/receive_report.py tests/results/fx2-20261021/R1 [--table]
```

.NET SDK 9、Node.js、Python と `tests/requirements.txt` のパッケージを使います。
Chromium/Chrome が必要です。別の実行ファイルを使うときは `PLAYWRIGHT_CHROMIUM_EXECUTABLE` を指定します。

```bash
dotnet build Xfer.csproj
dotnet run --project tests/Xfer.Regression.csproj -c Release
node --test tests/optical-core.test.js
python3 tests/browser_regression.py
python3 tests/app_smoke.py
```

C# は C# 5 構文でコンパイルし、Fountain、損失・重複・順序入替、QR の互換性、
受信ファイルの一致、破損拒否、選択の検証と保存先の競合回避を確認します。
JavaScript は20件です。ブラウザ回帰試験は実 WASM と合成 QR／仮想カメラを使い、API をモックします。
`app_smoke.py` は実サーバを別ポートで起動し、実際の QR 描画・終了、削除 API の拒否と
別アプリのプロセスを置換しないことを確認します。`DOTNET_BIN` で dotnet の場所を指定できます。

登録定義・削除定義・PowerShell 構文・HKCU の範囲・メニュー16行制限は次で確認します。
レジストリへの書込みは行いません。Linux の PowerShell 7 でも実行できます。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\test-desktop-context.ps1
```

デスクトップの画面とネイティブ部品は次で確認します。Xfer に同梱した MiniUI の DLL を使います。

```bash
dotnet run --project tests/Xfer.MiniUI.Probe.csproj -c Release
python tests/ui_drive.py               # Windows: runtime/windows-x64 と ui/MiniUI.App.dll を使う
python3 tests/ui_drive.py --linux      # Linux: ./Xfer.sh を使う
```

`Xfer.MiniUI.Probe.csproj` は、画面に出す QR（QrStream と ScaledQr）をカメラ相当の画像に置いて
デスクトップの受信処理（CameraReceiver）に渡し、受け取ったファイルが元と一致することを確かめます。
`ui_drive.py` は MiniUI の MCP で画面を操作し、送る・受け取る・スマホ・QR 表示をライトとダークで
`tests/results/screens` に保存します。

2026-10-20：MiniUI の repo と部品ビルド用ソースを bubblewrap のマウント名前空間内で隠し、
ビルドキャッシュの無い Xfer の写しから `./Xfer` のビルド・起動を確認しました。
独立した Xvfb 上で、フォルダ選択ボタン→実 zenity ダイアログの OK→README.md の選択、
選択解除・再選択、QR 表示・停止、送受信／スマホ画面の切替、テーマ切替、終了コード0とポート閉鎖を確認。
同じ隔離環境で `Xfer.MiniUI.Probe.csproj` も PASS（合成画像118枚、4.9秒、受信4ファイルが元と一致）。
記録と画像はローカルの `tests/results/miniui-bundled-20261020/`。Windows の更新後ビルド・起動、
通常デスクトップの portal 経路、実カメラ・実スマホはこの確認に含みません。

## 診断ログの隔離確認

`Xfer.Headless.Probe.csproj` は同じ `NativeLoopbackProbe` を、MiniUIソース・実カメラ・窓なしで実行します。
`TMPDIR` と `Xfer_LOG_DIR` を試験フォルダに設定し、`--artifacts-path` でビルド先も分離できます。
これは合成カメラ画像による試験であり、物理カメラでの転送成功や配布版MiniUIの動作を示しません。

`Xfer.Diagnostics.csproj` は引数で指定した隔離フォルダに試料・ログ・保存結果を作り、
製品の送信／受信処理からの成功・中止・重複・拒否・破損検出・保存失敗、秘密値の非記録、
JSONL形式、キュー上限・ローテーション、集計呼出しの負担を確認します。

```bash
dotnet run --project tests/Xfer.Diagnostics.csproj -c Release -- /path/to/isolated/log-test
```

`logging_integration.py` は隔離publishしたHTTP版をランダムな空きポートで起動します。
実HTTP・実WASM・canvasによる合成カメラ・実C#復元／保存を通し、バイト一致、ログの照合、
通信待ち／通信断中のIndexedDB保存、PCへの自動回収、16,384件の通常履歴を上書きした後も残る障害履歴、ストレージ拒否を確認します。
試験が起動したPIDだけを終了します。既存アプリのポートや配布フォルダは指定しないでください。

```bash
dotnet publish Xfer.csproj -c Release --no-self-contained -o /path/to/isolated/http
python3 tests/logging_integration.py --app /path/to/isolated/http --work /path/to/isolated/evidence
```

2026-10-19のログ実装確認では、C#回帰10件・JS20件・Chromium/WASM回帰・上記の隔離試験を実行しました。
成功／中止／破損／保存失敗、PWA通信保留・切断、ログ上限・秘密値非記録を確認しています。
Windowsネイティブ部品はソースのmanagedコンパイルまで、PowerShellはLinuxの7.6.5で確認しました。
`diagnostics_collection.py --stage /path/to/isolated/linux-distribution --work /path/to/evidence` は
隔離したXvfbで通常の `Xfer.sh` → Linux MiniUI → 実サーバを起動し、PWAの起動・通信断・復旧履歴が
PCへ自動保存されることを確認します。回収用のコンソールコマンドは実行しません。
実workerのWASM取得503、原因の安全な記録、同origin検査・容量制限・秘密値除去も確認します。
使用するXvfb、アプリ、ブラウザだけを起動し、それぞれ自分のPIDで片づけます。物理カメラは使いません。

10/19 のログ実装確認では、Linux MiniUI は `c0cc050` のソース入力でビルド・隔離起動を確認しました。
10/20 にホストの API を適応し、MiniUI の参照を `runtime/linux-x64` の DLL に切り替えました。
現在の同梱版は `runtime/linux-x64/MINIUI_SOURCE_REVISION` に記録しています。
Windows Native AOT配布物・PowerShell 5.1での起動、物理カメラ・スマホ実機・Tailscale実接続は別途確認が必要です。

`Xfer.Shutdown.csproj` は隔離フォルダ・専用ポートでNativeAppの実サーバを起動し、
終了直前のイベント・集計の保存とサーバ終了を確認します。書込みゲートを占有させ、
呼出元の待ちが上限で戻ることと、占有解除後に末尾が排出されることも確認します。
これはmanaged側の確認です。Native AOTの `xfer_shutdown` とPowerShell 5.1の事前コンパイル済み接続DLLは、
Windows通常配布物で `launcher.native_shutdown` のcode0、traceの `app.stop` / `native.shutdown.end`、
所有PID残存0を別途確認してください。コンパイルは配布ビルド時だけです。

## 大容量・ストリーミング光学転送

カメラなしの大容量試験、2,001ファイル・欠落再送・破損拒否・メモリ測定と
5.7 MiBの変更前後比較は [streaming-optical.md](../docs/streaming-optical.md) を参照してください。
`Xfer.Streaming.csproj` の `fx2` は全バイトの画面セル符号化、`picture` は
画面全体のラスタ生成からマーカー検出・受信ファイル保存までを通します。
大容量の作業先はrepo外を指定してください。
