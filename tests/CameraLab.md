# C920 CameraLab

Windows / .NET SDK 9 用の独立した測定装置。製品の既定設定は変更しない。
`capture`・`formats`・`focus-hold` はカメラを開くので、使用前・解放後に Room で @iris に報告し、
他の担当者の使用中には実行しない。`selftest` / `--help` / ビルドはカメラを使わない。
CameraLab 同士は名前付き mutex で同時実行を拒否するが、他のアプリとの調整は別途必要。

## ビルドと起動（PowerShell、repo 直下）

```powershell
dotnet build tests/Xfer.CameraLab.csproj -c Release --artifacts-path tests/results/cameralab-build-v2
$cameraLab = "tests/results/cameralab-build-v2/bin/Xfer.CameraLab/release/Xfer.CameraLab.dll"
dotnet $cameraLab selftest
dotnet $cameraLab --help
dotnet $cameraLab list
```

`list` は名前と短い識別子を表示する。既定では名前に C920 を含む1台を選ぶ。
CAMERA は名前の一部または識別子。0台・複数台に一致した場合は失敗し、別カメラに代替しない。
ビルドを分離しているため、製品の配布バイナリや他の試験の出力を上書きしない。

## 形式一覧

カメラの使用順が来てから実行する。

```powershell
dotnet $cameraLab formats C920 --out tests/results/c920-native-formats.json
```

解像度、fps の分子・分母・小数値、subtype の GUID と FourCC（MJPG、YUY2 等）を出力する。
`Advertises60Fps` は 60 との差が0.1未満の形式が列挙されたかどうか。
これはドライバが提示する形式であり、実測の取得速度ではない。
`formats` は既存の出力ファイルを上書きしない。
列挙は Media Foundation の
[GetNativeMediaType](https://learn.microsoft.com/en-us/windows/win32/medfound/processing-media-data-with-the-source-reader)
を利用する。C920 の60fps対応の有無は実機でこのコマンドを実行して確認する。

## 撮影・測定

```powershell
# 自動露出・自動ピント。最初の2秒を別集計し、その後10秒を測定。
dotnet $cameraLab capture tests/results/c920-auto-01 1920 30 10 C920 --height 1080 --warmup 2 --exposure auto --focus auto --gain keep --row-diffs

# 固定値の設定機能を使う例。最適値を保証する例ではない。
dotnet $cameraLab capture tests/results/c920-fixed-01 1920 30 10 C920 --height 1080 --exposure -7 --focus 20 --gain 0 --probe --save-all 2 --max-misses 5
```

構文は `capture OUT WIDTH FPS SECONDS [CAMERA] [options]`。
OUT は新規または空のフォルダ。既存の測定に追記・上書きしない。
SECONDS はウォームアップ後の測定時間。起動にかかった時間は含めず、
最初のフレーム受信を起点に `warmup + seconds` 秒間記録する。
最初の画像は設定適用前に撮られたため `pre_controls` として別扱いする。

| 指定 | 意味 |
|---|---|
| WIDTH / `--height N` | 横幅と高さ。高さ省略時は指定幅に合う形式を選ぶ。高さも固定したい比較では必ず指定 |
| FPS | 小数も可（例29.97、59.94）。差0.05fps以内のネイティブ形式を要求。なければ失敗 |
| `--focus keep\|auto\|N` | 現状維持、自動、カメラ単位の手動値。既定keep |
| `--exposure keep\|auto\|N` | 現状維持、自動、log2秒の手動値（−7は1/128秒）。既定keep |
| `--gain keep\|auto\|N` | 現状維持、自動、カメラ単位の手動値。既定keep。非対応の自動指定は失敗として記録 |
| `--warmup S` | 初期区間の秒数。既定2。0も可 |
| `--modules auto\|N` | QRの一辺のマス数（余白を含めない）。既定auto。数値は21,25,…,177 |
| `--queue N` | 解析待ち画像の上限。既定8、1～128。満杯ならその画像を落として数を記録 |
| `--save-all N` | 最初のN枚のうち解析に回った画像をPGM保存。既定0 |
| `--max-misses N` | 読取失敗画像を最大N枚保存。既定5。400枚を自動保存していた旧装置とは異なる |
| `--save-every N` | 元の取得indexがNの倍数である解析画像を保存。既定0＝無効 |
| `--probe` | 設定読戻しをコンソールにも表示。ファイルには指定なしでも記録 |
| `--no-restore` | 終了時の元設定への復元を省略。通常は指定しない |
| `--roi X,Y,W,H` | 行差分に使う固定QR領域。指定なしなら最初に読めたXfer QRの領域を固定 |
| `--row-diffs` | 境目候補に加え、各行の差分量をJSONL保存 |
| `--af-lock-after S` | 後述のAF固定試験を有効化し、最初のフレームからS秒以降の読取成功を待つ |
| `--af-lock-settle S` | 固定直後に前後比較から除く秒数。既定0.5 |

設定はカメラの範囲・step・対応モードを調べ、非対応値を黙って丸めない。
設定直後と約500msごとに値・auto/manualのフラグを読み戻す。
設定成功でも読戻しが違えば終了コード3。自動制御中の「値の変化」は正常として扱い、
自動モードの維持を確認する。keep はその設定を直接変更しない意味であり、
他の自動制御との連動で値が動くことを禁止する指定ではない。

元設定は、どれか一つを変更する**前に3項目すべて**記録する。
終了時は、連動で変わりうるkeepの項目も含めて復元する。
`RestorationSucceeded` は設定API成功と直後の読戻し一致を表す。
ドライバが後から値を変える可能性は残る。実機ではゲイン10→9、150→144という
遅れた読戻し変化を観測した。指定値と物理的な露光・ピントが同じとは断定しない。

## AFで読めた後にその値を固定する

```powershell
dotnet $cameraLab capture tests/results/c920-af-lock-01 1920 30 20 C920 --height 1080 --warmup 2 --exposure auto --gain keep --af-lock-after 5 --af-lock-settle 0.5 --row-diffs
```

この例はfocusを自動にして撮影し、最初のフレームから5秒以降にXfer QRが読めたら、
次のカメラ処理で現在のfocusを読み、**その値**をmanualで設定する。
読取成功はAFが最良の焦点に到達した保証ではなく、固定のきっかけとして使用する。
成功画像の撮影時と設定時には遅れがあるので、controls.jsonl の `af_lock` に
成功画像index・取得時刻・解析完了時刻・固定時刻・固定直前と直後の値を残す。

summary.json の `AfTrial.AutoMeasurement` と `AfTrial.LockedMeasurement` で前後の読取率を比較する。
両方とも通常の測定区間内だけを集計し、固定を実行した画像と固定後0.5秒は前後比較から除く。
frames.csv の `focus_phase` は `auto / lock_transition / lock_settling / locked / lock_failed`。
測定時間内に読めなかった、固定に失敗した、前後の片方に解析画像がない場合は
`AfTrial.Completed=false`、終了コード3。自動露出のまま比較するか、露出・ゲインを固定して
ピントだけ比較するかは実験側で指定する。

## 結果ファイル

| ファイル | 内容 |
|---|---|
| `run.json` | 指定条件、カメラ名・識別子、UTC開始時刻、実行DLLとZXingのSHA256、OS・runtime |
| `summary.json` | 実際の解像度・fps、取得数・解析前drop数、初期／測定／全体の集計、設定確認・復元結果、AF比較 |
| `captures.csv` | 取得した全画像のindex・取得時刻・間隔・区間・解析キュー投入可否 |
| `frames.csv` | 解析した画像の読取成否、session・sequence、payload SHA256、新規／重複、マス幅、明るさ、解析時間、AF区間、境目候補 |
| `controls.jsonl` | `before / apply / sample / af_lock / restore` ごとの設定値、範囲、step、対応フラグ、設定受付と不一致 |
| `row-differences.jsonl` | `--row-diffs` 時のみ。ROI、比較元index、各行の平均絶対差（0～255）、境目候補 |
| `frame-*.pgm` / `miss-*.pgm` | 解析に使った8bitグレー画像。ファイル名の番号は取得index |
| `trace/` | 製品側の診断機構が出力するカメラ等の診断ログ |

読取率 `DecodeRate` は「Xferのパケットとして検証できた枚数 / 解析した枚数」。
送信QR全数に対する受信率ではない。`QrDecodeRate` は他のQRも含むZXing成功率。
`DroppedBeforeDecode` と `MeasuredDropped` があれば、解析されなかった画像が存在する。
解像度・fpsの指定値とは別に `NegotiatedFps` と実際のコールバック間隔から求める `DeliveredFps` を残す。

`UniqueQr` はパケット全体のSHA256がその集計区間で初めて出た数、`DuplicateQr` は再登場数。
`UniqueSequences` はsession・payload FNV・長さ情報・sequenceの組合せによる別集計。
初期区間と測定区間はそれぞれ新規集合を持つため、両者のUniqueQrの和は全体のUniqueQrとは限らない。
frames.csv の `new_qr / duplicate_qr` は実行全体での初出判定。

`module_x_px / module_y_px` は読めたQRのファインダ中心間距離を `(一辺マス数−7)` で割った推定値。
autoでは二値画像からQR次元を検出する。余白をマス数に含めない。
斜め撮影のため縦横を別記し、読めない画像は空欄。summaryの中央値はXfer QRだけから計算する。
`decode_ms` と、次元検出等の `geometry_ms` は別々。行差分・ファイル保存時間はこれらに含めない。
明るさは画像全体を97画素間隔で標本化したmean/p5/p95。

装置は全画面・単一workerで解析する。製品のROI追跡・複数workerを含む受信速度や
ファイルの復元完了率を測る試験とは異なる。

## 上下の境目候補

連続して取得・解析できた2枚の画像について、固定ROIの各行の平均絶対差を計算する。
横方向はROI両端10%を除き、2画素間隔で標本化する。
行の差分量が上下2群に最も強く分かれる行を `boundary_candidate_row` に記録する。
上下の平均差が3階調以上、分割による説明分散比が0.15以上の場合のみ `boundary_row` にも記録する。
`boundary_confidence` はこの説明分散比で、正解確率ではない。
`boundary_delta` は下側平均差−上側平均差。行番号は画像上端からの0始まり。

ROI上下10%（最低8行）には境目を置かない。最良候補がその探索限界から2行以内なら
`edge_limited` として `boundary_candidate_row` だけを残し、`boundary_row` は空欄にする。
探索範囲によって位置が制限された候補を、画像内に見つかった境目と区別する。
1組につき最も強い境目候補を1つ出す。
背景・露出変化・ピント・動きでも段差が出るため、上下が異なるQRである証明ではない。
複数の境目や斜めの境目を確定するものでもない。`--row-diffs` の生の行差分と
PGMを合わせて判断する。未読状態で比較を始めたい場合は画像から `--roi` を指定する。

解析前dropでindexが飛んだ組は `boundary_reason=frame_gap` とし、推定しない。
他に `first_frame / no_qr_roi / size_changed / roi_out_of_bounds / low_contrast / edge_limited / weak_split / candidate_only` を記録する。
「連続」はアプリに届いた画像の連続を指し、USBやドライバより手前の欠落は判別しない。

## 終了と確認状況

Ctrl+Cは取得を止め、解析待ちを処理して結果を残す。
正常時はカメラ解放を待ってからsummaryを書き、`DeviceReleased=true` を記録する。
終了コード0＝測定完了、2＝引数・カメラ・記録エラー、3＝設定拒否／不一致／復元失敗／AF試験未完了、
130＝中断。読取0枚でも解析画像があれば、設定が成立している限り測定そのものは成功。
引数・カメラ選択・保存先検査で失敗した場合は結果フォルダが作られないことがある。

2026-10-21、このノートでビルド（警告0・エラー0）、selftest、既存回帰10件を確認。
基本機能のC920実機記録：

- `tests/results/cameralab-20261021-auto-final/`：1080p/30、露出auto・focus auto・gain keep。
  測定6秒、180枚中76枚読取、新規68・重複8、7.513×7.526px/マス、解析前drop0。
- `tests/results/cameralab-20261021-manual/`：固定露出−7・focus20・gain0を指定し、読戻しとCSV/JSON出力を確認。
- `tests/results/cameralab-20261021-manual-final/`：ゲイン10の指定後9への変化を記録し終了コード3。
- `tests/results/cameralab-20261021-auto/`：720p、ゲインauto非対応を検出。元設定の一括保存を修正する前の記録なので、
  復元結果の根拠には使用しない。

01:17のカメラ再使用許可後、追加した形式一覧・行差分・AF固定も実機確認した。
送り手の条件はRoomで伝えられた30fps・900px。記録は `tests/results/cameralab-20261021-0117/`。

- `native-formats.json`：335形式、MJPG/NV12/YUY2、最大30fps。現在の接続・ドライバでは60fps形式なし。
- `af-auto-01/`：1080p/30、AF成功後focus44に固定。測定区間内で自動時39/182（21.4%）、
  固定後221/402（55.0%）。固定直後0.5秒は比較から除外。
- `af-auto-02/`：同条件の再試行。focus44、自動時72/186（38.7%）、固定後175/398（44.0%）。
  いずれも露出auto・gain keepなので、ピントだけの因果効果や改善幅を確定する試験ではない。
- `boundary-final-02/`：探索限界の候補除外を入れた最終版。連続90枚・PGM90枚、解析前drop0。
  89組中、候補52、探索限界31、弱い分割5、低コントラスト1。候補行207～737、中央値542。
  生の画像差からの推定であり、上下で異なるQRの存在を確定する検査ではない。
- `boundary-raw-01/`：修正前の連続90枚と行差分。比較用に残した旧候補を最終版の判定と混ぜない。

各撮影フォルダには `analysis.json`、`seconds.csv`、`af-overview.png`、`boundary-overview.png` もある。
`report.json` は全試験の要点と送り手条件の記録。
AF試験2回と境目試験2回はすべて終了コード0・DeviceReleased=true。
最後の復元直後読戻しは露出−7/manual、focus20/manual、gain141/manual。

selftestではQRバージョン2/22、マス幅5/7px、重複・新規・失敗の分母、
合成画像の境目73行、同一画像・非連続画像・探索限界の扱い、AF値固定・設定連動・復元・値のずれの
シミュレーション、fps分数/FourCCを確認した。

## 保存済みデータからの集計・図

`camera_lab_report.py` はカメラを開かず、CSV/JSONの枚数・読取率・重複数・AF区間を独立に照合し、
`analysis.json` と1秒単位の `seconds.csv` を作る。`--plots` は追加でmatplotlib/numpyを使用する。
生成済みの集計・図は再実行時に更新する。

```powershell
python tests/camera_lab_report.py tests/results/cameralab-20261021-0117/af-auto-01
# このノートでは描画用パッケージを下記の隔離フォルダに配置済み。
$env:PYTHONPATH = (Resolve-Path tests/results/cameralab-python).Path
python tests/camera_lab_report.py --plots tests/results/cameralab-20261021-0117/af-auto-01
```
## 製品のピント固定・解除を単体で確認する

`focus-hold OUT [CAMERA]` は窓を出さず、製品の `MediaFoundationCamera` の
`IFocusHold` をworkerスレッドから呼ぶ実機試験です。C920の占有を事前に調整してください。
OUTは空のフォルダを指定します。開始時はAFが有効である必要があります。

```powershell
dotnet <ビルド先>/Xfer.CameraLab.dll focus-hold tests/results/focus-probe C920
```

現在値の手動固定、固定の維持、AF解除、元から手動の場合の無変更、終了時のAF復帰を確認します。
露出・ゲインは設定しません。試験後は開始時のfocusモードを復元し、カメラを解放します。
`controls.jsonl`に各段階の読戻し、`summary.json`に成否と解放結果、`trace/`に製品ログを残します。
終了コードは0=成功、3=検証失敗です。3枚連続・2秒の判断や読取改善そのものはこの試験の対象外です。

受け手の既存ログ `camera.focus_hold` / `camera.focus_release` は呼出しの記録です。
追加ログ `camera.focus.applied` の `Success`、`Value`、`Automatic` で実際の設定と読戻し結果を確認できます。
非対応機器・開始時に手動の機器は変更しません。Linux側はV4L2の
`VIDIOC_G_EXT_CTRLS` / `VIDIOC_S_EXT_CTRLS` と `FOCUS_AUTO` / `FOCUS_ABSOLUTE` を使い、
追加の `v4l2-ctl` コマンドは不要です。Linux実機の確認は別途必要です。
制御の定義は[Linux Camera Control Reference](https://docs.kernel.org/userspace-api/media/v4l/ext-ctrls-camera.html)、
[Windows IAMCameraControl](https://learn.microsoft.com/en-us/windows/win32/api/strmif/nn-strmif-iamcameracontrol)を参照してください。
