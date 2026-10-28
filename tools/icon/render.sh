#!/bin/sh
# Renders the app icon from its SVG sources. Run after editing an SVG here, then commit the PNGs.
#   sde-icon.svg        -> src/Sde.App/Assets/icon-1024.png (the .icns sizes from 64 px up, and Settings › About)
#   sde-icon-small.svg  -> tools/icon/icon-16.png, icon-32.png (hand-simplified for Finder lists and the menu bar)
#   social-preview.svg  -> docs/social-preview.png (GitHub's Settings › Social preview, 1280 x 640)
# package_app.sh builds out/icon/SDE.icns from these; delete out/icon after rendering so it rebuilds.
set -eu
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build tools/icon/IconRender -c Release -o out/iconrender >/dev/null
R="dotnet out/iconrender/IconRender.dll"
$R tools/icon/sde-icon.svg src/Sde.App/Assets/icon-1024.png 1024
$R tools/icon/sde-icon-small.svg tools/icon/icon-32.png 32
$R tools/icon/sde-icon-small.svg tools/icon/icon-16.png 16
$R tools/icon/social-preview.svg docs/social-preview.png 1280
rm -rf out/icon
