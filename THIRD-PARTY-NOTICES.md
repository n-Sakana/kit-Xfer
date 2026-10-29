# Third-party notices

Xfer includes or is based on the following software and fonts.

## MiniUI

The desktop rendering libraries are included in `runtime/linux-x64` and
`runtime/windows-x64`. See [runtime components](runtime/README.md) and the
[rendering library notices](runtime/MiniUI-THIRD-PARTY-NOTICES.md).

## Decimen Optical Transfer

Xfer is based on [bashalarmistalt/decimen-optical-transfer](https://github.com/bashalarmistalt/decimen-optical-transfer)
v0.3.0. The D1/0C QR transfer format is retained; Xfer also supports Tile and
the D1/0D streaming format.

- License: MIT
- Copyright (c) 2026 Evan Crawley (Bash Alarmist)
- Full license: [`LICENSE`](LICENSE)

## ZXing.Net

`lib/zxing.dll` provides QR encoding and decoding.

- Project: [micjahn/ZXing.Net](https://github.com/micjahn/ZXing.Net)
- License: Apache License 2.0
- Full license: [`lib/LICENSE-ZXing.Net.txt`](lib/LICENSE-ZXing.Net.txt)

## zxing-wasm

`web/zxing_reader-EOacYbLr.wasm` provides QR decoding in the browser.

- Project: [Sec-ant/zxing-wasm](https://github.com/Sec-ant/zxing-wasm)
- License: MIT
- Copyright (c) 2023 Ze-Zheng Wu
- Full license: [`lib/LICENSE-zxing-wasm.txt`](lib/LICENSE-zxing-wasm.txt)

## zxing-cpp

The QR decoding engine used by the included zxing-wasm library.

- Project: [zxing-cpp/zxing-cpp](https://github.com/zxing-cpp/zxing-cpp)
- License: Apache License 2.0
- Full license: [`lib/LICENSE-zxing-cpp.txt`](lib/LICENSE-zxing-cpp.txt)

## .NET runtime

`runtime/windows-x64/MiniUI.Engine.dll` and `ui/MiniUI.App.dll` contain
parts of the .NET 9 runtime, statically linked by Native AOT.

- Project: [dotnet/runtime](https://github.com/dotnet/runtime)
- License: MIT
- Copyright (c) .NET Foundation and Contributors
- License text: [rendering library notices](runtime/MiniUI-THIRD-PARTY-NOTICES.md)

## Inter

`ui/fonts/Inter-*.ttf` supplies the interface typeface.

- Project: [rsms/inter](https://github.com/rsms/inter)
- License: SIL Open Font License 1.1
- Copyright (c) 2016 The Inter Project Authors
- Full license: [`ui/fonts/LICENSE-Inter.txt`](ui/fonts/LICENSE-Inter.txt)

## Noto Sans JP

`ui/fonts/NotoSansJP-*.otf` supplies Japanese glyphs for user file names and content.

- Project: [notofonts/noto-cjk](https://github.com/notofonts/noto-cjk)
- License: SIL Open Font License 1.1
- © 2014-2021 Adobe (http://www.adobe.com/)
- Full license: [`ui/fonts/LICENSE-NotoSansJP.txt`](ui/fonts/LICENSE-NotoSansJP.txt)
