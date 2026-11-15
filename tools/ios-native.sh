#!/usr/bin/env bash
#
# Builds the iPad app's native Bluetooth library (src/GrimesGolf.Bluetooth/Apple/Native/gg_ble.m, which wraps
# CoreBluetooth for the game's C#) as a static library for the iPad: <output folder>/libggble.a.
# tools/ios-export.sh runs it and links the result into the app; .github/workflows/ios.yml runs it by itself too, as
# a quick check that it compiles. Needs macOS with Xcode.
#
#   tools/ios-native.sh <output folder>
set -euo pipefail

out="${1:?Usage: tools/ios-native.sh <output folder>}"
mkdir -p "$out"
source="$(cd "$(dirname "$0")/.." && pwd)/src/GrimesGolf.Bluetooth/Apple/Native"

# The same oldest iOS as the export preset's (application/min_ios_version).
xcrun --sdk iphoneos clang -arch arm64 -miphoneos-version-min=15.0 -fobjc-arc -fmodules -O2 -Wall \
    -c "$source/gg_ble.m" -o "$out/gg_ble.o"
rm -f "$out/libggble.a"
xcrun ar rcs "$out/libggble.a" "$out/gg_ble.o"
rm -f "$out/gg_ble.o"
echo "Made $out/libggble.a"
