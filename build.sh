#!/usr/bin/env bash
# Legacy default: build and install. --no-install builds without touching the game.
# Usage: ./build.sh [Release|Debug] [--game-dir PATH] [--no-install]
set -euo pipefail
cd "$(dirname "$0")"
CONFIG=Release
GAME_DIR="${STS2_GAME_DIR:-}"
INSTALL=1
while (($#)); do
  case "$1" in
    Release|Debug) CONFIG="$1"; shift ;;
    --game-dir)
      if (($# < 2)); then echo 'ERROR: --game-dir requires a path' >&2; exit 2; fi
      GAME_DIR="$2"; shift 2 ;;
    --no-install) INSTALL=0; shift ;;
    *) echo "ERROR: unknown argument: $1" >&2; exit 2 ;;
  esac
done
case "$(uname -s)" in
  Darwin*)
    GAME_DIR="${GAME_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2}"
    DATA_DIR="$GAME_DIR/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64"
    MODS_DIR="$GAME_DIR/SlayTheSpire2.app/Contents/MacOS/mods" ;;
  Linux*)
    GAME_DIR="${GAME_DIR:-$HOME/.local/share/Steam/steamapps/common/Slay the Spire 2}"
    DATA_DIR="$GAME_DIR/data_sts2_linuxbsd_x86_64"
    MODS_DIR="$GAME_DIR/mods" ;;
  *)
    GAME_DIR="${GAME_DIR:-C:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2}"
    # Git Bash accepts Windows paths for dotnet; use slash paths for shell checks.
    if command -v cygpath >/dev/null 2>&1; then GAME_DIR="$(cygpath -u "$GAME_DIR")"; fi
    DATA_DIR="$GAME_DIR/data_sts2_windows_x86_64"
    MODS_DIR="$GAME_DIR/mods" ;;
esac
for dll in sts2.dll GodotSharp.dll 0Harmony.dll Steamworks.NET.dll; do
  if [[ ! -f "$DATA_DIR/$dll" ]]; then
    echo "ERROR: missing game assembly: $DATA_DIR/$dll. Set STS2_GAME_DIR or pass --game-dir." >&2
    exit 1
  fi
done
if ! command -v dotnet >/dev/null 2>&1; then
  echo 'ERROR: dotnet not found. Install .NET 9 SDK.' >&2
  exit 1
fi
OUT_DIR="$PWD/out/RetryTheSpire"
echo "Building Retry the Spire ($CONFIG) using $GAME_DIR"
BUILD_GAME_DIR="$GAME_DIR"
if command -v cygpath >/dev/null 2>&1; then BUILD_GAME_DIR="$(cygpath -w "$GAME_DIR")"; fi
dotnet build RetryTheSpire.csproj -c "$CONFIG" -o "$OUT_DIR" -p:STS2GameDir="$BUILD_GAME_DIR"
if ((INSTALL)); then
  INSTALL_DIR="$MODS_DIR/RetryTheSpire"
  mkdir -p "$INSTALL_DIR"
  cp "$OUT_DIR/RetryTheSpire.dll" "$INSTALL_DIR/"
  cp mod_manifest.json "$INSTALL_DIR/"
  echo "Installed to $INSTALL_DIR"
fi
