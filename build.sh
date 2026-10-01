#!/usr/bin/env bash
# Build RetryTheSpire.dll and install it to the game's mods/ folder.
# Usage: ./build.sh [Release|Debug]   (default Release)

set -euo pipefail
cd "$(dirname "$0")"

CONFIG="${1:-Release}"

GAME_DIR="${STS2_GAME_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2}"
case "$(uname -s)" in
  Darwin*)  MODS_DIR="$GAME_DIR/SlayTheSpire2.app/Contents/MacOS/mods" ;;
  Linux*)   MODS_DIR="$GAME_DIR/mods" ;;
  *)        MODS_DIR="$GAME_DIR/mods" ;;
esac

OUT_DIR="$PWD/out/RetryTheSpire"
INSTALL_DIR="$MODS_DIR/RetryTheSpire"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "ERROR: dotnet not found. Install .NET 9 SDK: https://dotnet.microsoft.com/download/dotnet/9.0" >&2
  exit 1
fi

echo "=== Building Retry the Spire ($CONFIG) ==="
echo "Game dir:   $GAME_DIR"
echo "Build out:  $OUT_DIR"
echo "Install to: $INSTALL_DIR"
echo

rm -rf "$OUT_DIR"
dotnet build RetryTheSpire.csproj -c "$CONFIG" -o "$OUT_DIR" -p:STS2GameDir="$GAME_DIR"

echo
echo "=== Installing ==="
mkdir -p "$INSTALL_DIR"
cp "$OUT_DIR/RetryTheSpire.dll" "$INSTALL_DIR/"
cp mod_manifest.json "$INSTALL_DIR/"
echo "Installed:"
ls -la "$INSTALL_DIR/"
echo
echo "Done. Launch STS2 to load the mod."
