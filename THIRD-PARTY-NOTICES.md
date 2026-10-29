# 第三者ソフトウェアの表示 / Third-party notices

Xfer は、次の第三者ソフトウェアと書体を基礎とするか、同梱しています。

## MiniUI

画面の表示に使うビルド済み部品を `runtime/linux-x64` と `runtime/windows-x64` に同梱しています。
版と更新方法は [`runtime/README.md`](runtime/README.md)、
部品に付属する第三者表示は [`runtime/MiniUI-THIRD-PARTY-NOTICES.md`](runtime/MiniUI-THIRD-PARTY-NOTICES.md) を参照してください。

## Decimen Optical Transfer

Xfer は [bashalarmistalt/decimen-optical-transfer](https://github.com/bashalarmistalt/decimen-optical-transfer)
v0.3.0 の fork です。上流のワイヤーフォーマットには手を入れていません。

- License: MIT
- Copyright (c) 2026 Evan Crawley (Bash Alarmist)
- ライセンス全文: [`LICENSE`](LICENSE)

## ZXing.Net

`lib/zxing.dll` を QR コードの生成と読み取りに同梱しています。

- Project: [micjahn/ZXing.Net](https://github.com/micjahn/ZXing.Net)
- License: Apache License 2.0
- ライセンス全文: [`lib/LICENSE-ZXing.Net.txt`](lib/LICENSE-ZXing.Net.txt)

## zxing-wasm

`web/zxing_reader-EOacYbLr.wasm` をブラウザでの QR コード読み取りに同梱しています。

- Project: [Sec-ant/zxing-wasm](https://github.com/Sec-ant/zxing-wasm)
- License: MIT
- Copyright (c) 2023 Ze-Zheng Wu
- ライセンス全文: [`lib/LICENSE-zxing-wasm.txt`](lib/LICENSE-zxing-wasm.txt)

## zxing-cpp

同梱する zxing-wasm の QR 読み取りエンジンです。

- Project: [zxing-cpp/zxing-cpp](https://github.com/zxing-cpp/zxing-cpp)
- License: Apache License 2.0
- ライセンス全文: [`lib/LICENSE-zxing-cpp.txt`](lib/LICENSE-zxing-cpp.txt)

## MiniUI

デスクトップの画面は kit の MiniUI で表示します。Windows では `runtime/windows-x64/MiniUI.Engine.dll` と
`MiniUI.Bridge.dll` を同梱し、Linux では MiniUI のソースから一緒にビルドします。
MiniUI 自身の権利表示は MiniUI リポジトリの `THIRD-PARTY-NOTICES.md` にあります。

## .NET ランタイム

`runtime/windows-x64/MiniUI.Engine.dll` と `ui/MiniUI.App.dll` は Native AOT で作るため、
.NET 9 ランタイムライブラリの一部を含みます。

- Project: [dotnet/runtime](https://github.com/dotnet/runtime)
- License: MIT
- Copyright (c) .NET Foundation and Contributors

## Inter

画面の欧文書体として `ui/fonts/Inter-*.ttf` を同梱しています。

- Project: [rsms/inter](https://github.com/rsms/inter)
- License: SIL Open Font License 1.1
- Copyright (c) 2016 The Inter Project Authors
- ライセンス全文: [`ui/fonts/LICENSE-Inter.txt`](ui/fonts/LICENSE-Inter.txt)

## Noto Sans JP

画面の和文書体として `ui/fonts/NotoSansJP-*.otf` を同梱しています。

- Project: [notofonts/noto-cjk](https://github.com/notofonts/noto-cjk)
- License: SIL Open Font License 1.1
- © 2014-2021 Adobe (http://www.adobe.com/)
- ライセンス全文: [`ui/fonts/LICENSE-NotoSansJP.txt`](ui/fonts/LICENSE-NotoSansJP.txt)
