#!/usr/bin/env bash
#
# Builds the iPad app's native Bluetooth library (src/GrimesGolf.Bluetooth/Apple/Native/gg_ble.m, which wraps
# CoreBluetooth for the game's C#) as static libraries, one folder per .NET runtime identifier because Godot builds
# the game's code for the iPad and for both simulators:
#
#   <output folder>/ios-arm64/libggble.a   <output folder>/iossimulator-arm64/libggble.a   <output folder>/iossimulator-x64/libggble.a
#
# tools/ios-export.sh runs it and points the game's project file at the result (GG_BLE_LIBS), which links it into the
# game's own library; .github/workflows/ios.yml runs it by itself too, as a quick check that it compiles.
# Needs macOS with Xcode.
#
#   tools/ios-native.sh <output folder>
set -euo pipefail

out="${1:?Usage: tools/ios-native.sh <output folder>}"
source="$(cd "$(dirname "$0")/.." && pwd)/src/GrimesGolf.Bluetooth/Apple/Native"

# The same oldest iOS as the export preset's (application/min_ios_version).
build() {   # runtime identifier, SDK, clang target
    local rid="$1" sdk="$2" target="$3"
    mkdir -p "$out/$rid"
    xcrun --sdk "$sdk" clang -target "$target" -fobjc-arc -fmodules -O2 -Wall -c "$source/gg_ble.m" -o "$out/$rid/gg_ble.o"
    rm -f "$out/$rid/libggble.a"
    xcrun ar rcs "$out/$rid/libggble.a" "$out/$rid/gg_ble.o"
    rm -f "$out/$rid/gg_ble.o"
    echo "Made $out/$rid/libggble.a"
}
build ios-arm64 iphoneos arm64-apple-ios15.0
build iossimulator-arm64 iphonesimulator arm64-apple-ios15.0-simulator
build iossimulator-x64 iphonesimulator x86_64-apple-ios15.0-simulator
