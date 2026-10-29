# MiniUI の同梱部品

起動・Xfer のビルド・テストは、このフォルダの部品を使います。MiniUI の repo は読みません。

- `linux-x64/MiniUI.Core.dll` と `MiniUI.Platform.Linux.dll`：MiniUI `de07be7` から .NET SDK 9 / Release でビルド。完全な commit ID は `linux-x64/MINIUI_SOURCE_REVISION`。
- `windows-x64/MiniUI.Engine.dll` と `MiniUI.Bridge.dll`：既存の配布部品を保持。Xfer の commit `9a477499d11a330c7f95a0112fcfd99e4d06cc8f` にあるものと同一。元の MiniUI commit ID は未記録。
- `MiniUI-SHA256SUMS`：同梱部品の SHA-256。repo ルートで `sha256sum -c runtime/MiniUI-SHA256SUMS` により照合できます。
- `MiniUI-THIRD-PARTY-NOTICES.md`：MiniUI に付属する第三者表示。

## 更新するとき

通常のアプリビルドとは別に行います。更新する MiniUI の版を選び、別の作業フォルダでビルドしてください。

1. Linux：.NET SDK 9 で `dotnet build src/MiniUI.Platform.Linux/MiniUI.Platform.Linux.csproj -c Release -o <部品の出力先>` を実行し、出力された `MiniUI.Core.dll` と `MiniUI.Platform.Linux.dll` を **一緒に** `runtime/linux-x64/` へコピーします。
2. Windows：選んだ MiniUI の版を Windows 上で `tools\build.cmd` によりビルドし、`dist\windows-x64\aot` の `MiniUI.Engine.dll` と `MiniUI.Bridge.dll` を **一緒に** `runtime/windows-x64/` へコピーします。UI 試験用の `miniui.cmd` も `tests/miniui.cmd` へコピーします。
3. 各 OS の `MINIUI_SOURCE_REVISION` に元の完全な commit ID を記録し、第三者表示と `MiniUI-SHA256SUMS` も更新します。Xfer 自身の DLL（`Xfer.Launcher.dll`、`ui/MiniUI.App.dll`）は MiniUI の部品ではありません。
4. Xfer の Linux / Windows ビルドを実行し、MiniUI の repo が無い環境で起動、ファイル選択、QR 表示・終了を確認します。API が変わったときは Xfer のホストも合わせて更新します。

部品を入れ替えるまで、MiniUI 側の変更が Xfer に自動で入ることはありません。

Linux の同梱 DLL は、CodeView の PDB 参照パスだけを `/_/Xfer/miniui-de07be7/` 配下へ正規化しています。
MiniUI のコード・アセンブリ識別子・ソース版は変更していません。チェックサムは正規化後のファイルに対応します。
