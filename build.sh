#!/bin/bash
# Build Warm Start against the local KSP install.
#
#   ./build.sh            build into GameData/WarmStart here, then install it into KSP
#   ./build.sh check      compile only, install nothing
#   ./build.sh dist       build into GameData/ here without installing anything
#
# It is built against Keystone.dll (https://github.com/IshiakiZ/ksp-keystone), looked for in this order:
# KEYSTONE=/path/to/Keystone.dll, a built copy of that repository beside this one, the copy installed in the game.
# Needs the .NET SDK (`brew install dotnet`). Override the game location with KSP_DIR=/path/to/KSP.
set -euo pipefail

cd "$(dirname "$0")"
MODE="${1:-install}"
. tools/build/common.sh
OUT=GameData/WarmStart

KEYSTONE="${KEYSTONE:-}"
for candidate in ../ksp-keystone/GameData/Keystone/Keystone.dll "$KSP_DIR/GameData/Keystone/Keystone.dll"; do
  [ -z "$KEYSTONE" ] && [ -f "$candidate" ] && KEYSTONE="$candidate"
done
[ -n "$KEYSTONE" ] && [ -f "$KEYSTONE" ] || { echo "error: Keystone.dll not found: install Keystone into the game, build ../ksp-keystone, or set KEYSTONE=/path/to/Keystone.dll" >&2; exit 1; }

case "$MODE" in
  check)
    WITH="$KEYSTONE" compile "$TMP/WarmStart.dll" "" src/WarmStart
    echo "ok: compiles"
    ;;
  dist|install)
    mkdir -p "$OUT"
    WITH="$KEYSTONE" compile "$OUT/WarmStart.dll" "" src/WarmStart
    # (the mod's own shaders, if it has any: made by tools/shaderpack/make_bundle.py and kept ready made in src/WarmStart/Shaders)
    if ls src/WarmStart/Shaders/*.bundle >/dev/null 2>&1; then mkdir -p "$OUT/PluginData"; cp src/WarmStart/Shaders/*.bundle "$OUT/PluginData/"; fi
    if [ "$MODE" = dist ]; then echo "ok: built into $OUT"; exit 0; fi
    mkdir -p "$KSP_DIR/GameData/WarmStart"
    cp "$OUT/WarmStart.dll" "$KSP_DIR/GameData/WarmStart/"
    if [ -d "$OUT/PluginData" ]; then mkdir -p "$KSP_DIR/GameData/WarmStart/PluginData"; cp "$OUT/PluginData"/*.bundle "$KSP_DIR/GameData/WarmStart/PluginData/"; fi
    echo "ok: Warm Start installed to $KSP_DIR/GameData/WarmStart (restart KSP to load it; it needs Keystone there too)"
    ;;
  *)
    echo "usage: ./build.sh [install|check|dist]" >&2; exit 2
    ;;
esac
