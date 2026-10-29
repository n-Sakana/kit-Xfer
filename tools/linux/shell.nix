{ pkgs ? import <nixpkgs> {}, XferRoot ? "", arguments ? "" }:
pkgs.mkShell {
  packages = with pkgs; [ dotnet-sdk_9 gtk3 zenity xdg-utils ffmpeg ];
  LD_LIBRARY_PATH = pkgs.lib.makeLibraryPath (with pkgs; [
    gtk3 glib pango cairo freetype harfbuzz fontconfig libx11
  ]);
  Xfer_SOURCE_ROOT = XferRoot;
  Xfer_ARGUMENTS_BASE64 = arguments;
}
