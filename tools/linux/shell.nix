{ pkgs ? import <nixpkgs> {}, XferRoot ? "", arguments ? "", withBuildTools ? false }:
pkgs.mkShell {
  packages = with pkgs; [ zlib openssl icu zenity xdg-utils ffmpeg ]
    ++ lib.optionals withBuildTools [ dotnet-sdk_9 patchelf clang ];
  LD_LIBRARY_PATH = pkgs.lib.makeLibraryPath (with pkgs; [
    zlib openssl icu stdenv.cc.cc.lib libx11
  ]);
  Xfer_SOURCE_ROOT = XferRoot;
  Xfer_ARGUMENTS_BASE64 = arguments;
}
