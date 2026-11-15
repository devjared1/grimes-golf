#!/usr/bin/env bash
#
# Builds the iPad app on a Mac: exports the "iOS" preset (src/GrimesGolf.Godot/export_presets.cfg) as an Xcode
# project with the Godot .NET editor, builds that with Xcode, and packs the result as an .ipa.
# .github/workflows/ios.yml runs it on GitHub's Macs; docs/ipad-testing.md says what to do with the .ipa.
#
#   tools/ios-export.sh <output folder>
#
# Needs macOS with Xcode, the .NET 8 SDK, and the Godot 4.7.2 .NET editor with its iOS export template installed.
# Settings, from the environment:
#   GODOT             the Godot .NET editor (default: godot)
#   IOS_BUNDLE_ID     the app's bundle identifier (default: the preset's placeholder, com.grimesgolf.app)
#   IOS_BUILD_NUMBER  the build number, which Apple wants higher for every upload (default: 1)
#   IOS_RENDERER      forward_plus or mobile (default: the project's, rendering_method.mobile in project.godot)
#
# Unsigned by default: that build can't be installed as it is, and Sideloadly signs it with an Apple ID as it installs
# it. Given IOS_PROFILE_NAME and the settings below it makes a build signed for the App Store and TestFlight instead
# (docs/ipad-release.md), which needs the Apple Developer account's certificate and profile installed on the Mac:
#   IOS_TEAM_ID           the Apple Developer team ID
#   IOS_SIGN_IDENTITY     the distribution certificate's name in the keychain ("Apple Distribution: ...")
#   IOS_PROFILE_NAME      the name of the App Store provisioning profile
#   IOS_UPLOAD_KEY_FILE   optional: an App Store Connect API key (.p8). With IOS_UPLOAD_KEY_ID and IOS_UPLOAD_ISSUER_ID
#                         the build goes to TestFlight and no .ipa is kept; without it GrimesGolf-signed.ipa is.
set -euo pipefail

out="${1:?Usage: tools/ios-export.sh <output folder>}"
mkdir -p "$out/logs"
out="$(cd "$out" && pwd)"
root="$(cd "$(dirname "$0")/.." && pwd)"
project="$root/src/GrimesGolf.Godot"
godot="${GODOT:-godot}"
name="GrimesGolf"   # the Xcode project, scheme and .app (the name under the icon is the project's, "Grimes Golf")

# Apple's version numbers are digits and dots only: "1.0.0-beta" goes in as 1.0.0 (the app still shows the whole version).
version="$(sed -n 's/^config\/version="\(.*\)"/\1/p' "$project/project.godot")"
export IOS_SHORT_VERSION="${version%%-*}"
export IOS_BUILD_NUMBER="${IOS_BUILD_NUMBER:-1}"
echo "Grimes Golf $version for iPad (version $IOS_SHORT_VERSION, build $IOS_BUILD_NUMBER)"

# The preset and project settings are changed for this build only, and put back afterwards.
cp "$project/export_presets.cfg" "$out/logs/export_presets.cfg.orig"
cp "$project/project.godot" "$out/logs/project.godot.orig"
restore() {
    cp "$out/logs/export_presets.cfg.orig" "$project/export_presets.cfg"
    cp "$out/logs/project.godot.orig" "$project/project.godot"
    rm -f "$out/logs/export_presets.cfg.orig" "$out/logs/project.godot.orig" "$project/shipped.zip"
}
trap restore EXIT

# Sets key="value" in a Godot settings file (the value comes from the environment, so it needs no escaping here).
set_value() {   # file key variable
    KEY="$2" VAR="$3" perl -pi -e 'BEGIN { $k = quotemeta $ENV{KEY}; $v = $ENV{$ENV{VAR}}; } s/^$k=.*$/$ENV{KEY}="$v"/' "$1"
    grep -q "^$2=" "$1" || { echo "$2 isn't in $1" >&2; exit 1; }
}
# Godot won't export without a team ID, and signs its libraries with whatever identity is named. An unsigned build
# has neither: a made-up team, and "-" (no identity).
signed=false
if [ -n "${IOS_PROFILE_NAME:-}" ]; then
    signed=true
    : "${IOS_TEAM_ID:?IOS_TEAM_ID is needed to sign}" "${IOS_SIGN_IDENTITY:?IOS_SIGN_IDENTITY is needed to sign}"
    echo "Signed build for the App Store"
else
    export IOS_TEAM_ID="0000000000" IOS_SIGN_IDENTITY="-"
fi
set_value "$project/export_presets.cfg" application/app_store_team_id IOS_TEAM_ID
set_value "$project/export_presets.cfg" application/code_sign_identity_release IOS_SIGN_IDENTITY
set_value "$project/export_presets.cfg" application/short_version IOS_SHORT_VERSION
set_value "$project/export_presets.cfg" application/version IOS_BUILD_NUMBER
if [ -n "${IOS_BUNDLE_ID:-}" ]; then set_value "$project/export_presets.cfg" application/bundle_identifier IOS_BUNDLE_ID; fi
if [ -n "${IOS_RENDERER:-}" ]; then set_value "$project/project.godot" renderer/rendering_method.mobile IOS_RENDERER; fi
echo "Renderer: $(sed -n 's/^renderer\/rendering_method.mobile="\(.*\)"/\1/p' "$project/project.godot")"

# The courses and physics presets, which sit in folders beside the Windows app, go inside this one (ShippedFiles.cs).
(cd "$project" && rm -f shipped.zip && zip -qr shipped.zip courses tuning -x '*/.gdignore')

# The Bluetooth library for the Square (CoreBluetooth, in Objective-C). The game's project file links it into the
# game's own library (GG_BLE_LIBS), which Godot builds with the rest of the export.
"$root/tools/ios-native.sh" "$out/native"
export GG_BLE_LIBS="$out/native"

# The editor needs the game's code built before it can import and export the project.
dotnet build "$project/GrimesGolf.Godot.csproj" -c Debug -v q -nologo

echo "Importing the project's assets..."
"$godot" --headless --path "$project" --import > "$out/logs/godot-import.log" 2>&1 \
    || echo "(Godot reported a problem while importing; carrying on. See godot-import.log.)"

# The preset exports the Xcode project only (application/export_project_only): Godot's own Xcode build insists on
# signing. The game's C# is compiled here too, ahead of time, for the iPad and for both simulators.
echo "Exporting the Xcode project..."
rm -rf "$out/$name" "$out/$name.xcodeproj" "$out/$name.xcframework"
"$godot" --headless --path "$project" --export-release "iOS" "$out/$name.ipa" > "$out/logs/godot-export.log" 2>&1 \
    || { tail -n 60 "$out/logs/godot-export.log"; echo "Godot's export failed." >&2; exit 1; }
if [ ! -d "$out/$name.xcodeproj" ]; then
    tail -n 60 "$out/logs/godot-export.log"
    echo "Godot's export didn't make $name.xcodeproj." >&2
    exit 1
fi
# Godot reports an export "completed with warnings" even when building the game's code failed, which leaves an app
# without the game in it (it opens to nothing). So any build error it logged stops this build.
errors="$(find "$HOME/Library/Application Support/Godot/mono/build_logs" -name msbuild_issues.csv -exec grep -h '^error,' {} + 2>/dev/null | cut -c1-600 || true)"
if [ -n "$errors" ]; then
    printf '%s\n' "$errors"
    echo "Godot didn't build the game's code (see above, and the build logs)." >&2
    exit 1
fi
# Compiling ahead of time warns about code that might not survive it (the full list is in Godot's build log, kept
# with the run's logs). Godot's own C# glue always has some, and every JSON call in the game has two, which the
# workflow's JSON job shows are fine. Any others that name the game's code are worth reading, so they're printed.
issues="$(find "$HOME/Library/Application Support/Godot/mono/build_logs" -name msbuild_issues.csv 2>/dev/null | head -n 1)"
if [ -n "$issues" ]; then
    game="$(grep -E ',IL[0-9]+,"?GrimesGolf[.]' "$issues" || true)"
    other="$(printf '%s\n' "$game" | grep -v 'System[.]Text[.]Json' || true)"
    echo "Ahead-of-time warnings: $(grep -cE ',IL[0-9]+,' "$issues" || true) in all;" \
         "$(printf '%s\n' "$game" | grep -c 'System[.]Text[.]Json' || true) are the game's JSON calls," \
         "$(printf '%s\n' "$other" | grep -c . || true) more name the game's code, and the rest are Godot's own."
    printf '%s\n' "$other" | cut -c1-400 | head -n 20
fi

if $signed; then
    # Archive with the distribution certificate and the App Store profile named, then export (and upload) the archive.
    echo "Archiving with Xcode ($(xcodebuild -version | tr '\n' ' '))..."
    xcodebuild -project "$out/$name.xcodeproj" -scheme "$name" -sdk iphoneos -configuration Release \
        -destination "generic/platform=iOS" -derivedDataPath "$out/xcode" -archivePath "$out/$name.xcarchive" archive \
        CODE_SIGN_STYLE=Manual DEVELOPMENT_TEAM="$IOS_TEAM_ID" CODE_SIGN_IDENTITY="$IOS_SIGN_IDENTITY" \
        PROVISIONING_PROFILE_SPECIFIER="$IOS_PROFILE_NAME" > "$out/logs/xcodebuild.log" 2>&1 \
        || { tail -n 80 "$out/logs/xcodebuild.log"; echo "The Xcode archive failed." >&2; exit 1; }

    destination=export
    auth=()
    if [ -n "${IOS_UPLOAD_KEY_FILE:-}" ]; then
        destination=upload
        auth=(-allowProvisioningUpdates -authenticationKeyPath "$IOS_UPLOAD_KEY_FILE"
              -authenticationKeyID "${IOS_UPLOAD_KEY_ID:?IOS_UPLOAD_KEY_ID is needed to upload}"
              -authenticationKeyIssuerID "${IOS_UPLOAD_ISSUER_ID:?IOS_UPLOAD_ISSUER_ID is needed to upload}")
    fi
    bundle="$(/usr/libexec/PlistBuddy -c 'Print :ApplicationProperties:CFBundleIdentifier' "$out/$name.xcarchive/Info.plist")"
    options="$out/logs/ExportOptions.plist"
    cat > "$options" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>method</key><string>app-store-connect</string>
  <key>destination</key><string>$destination</string>
  <key>signingStyle</key><string>manual</string>
  <key>teamID</key><string>$IOS_TEAM_ID</string>
  <key>signingCertificate</key><string>$IOS_SIGN_IDENTITY</string>
  <key>provisioningProfiles</key><dict><key>$bundle</key><string>$IOS_PROFILE_NAME</string></dict>
  <key>uploadSymbols</key><false/>
</dict></plist>
PLIST
    echo "Exporting the archive (destination: $destination)..."
    rm -rf "$out/signed"
    xcodebuild -exportArchive -archivePath "$out/$name.xcarchive" -exportPath "$out/signed" \
        -exportOptionsPlist "$options" ${auth[@]+"${auth[@]}"} > "$out/logs/xcodebuild-export.log" 2>&1 \
        || { tail -n 60 "$out/logs/xcodebuild-export.log"; echo "Exporting (or uploading) the archive failed." >&2; exit 1; }
    if [ "$destination" = upload ]; then
        echo "Uploaded build $IOS_BUILD_NUMBER. It shows in TestFlight once Apple has processed it (5 to 30 minutes)."
    else
        mv "$out/signed/$name.ipa" "$out/$name-signed.ipa"
        echo "Made $out/$name-signed.ipa ($(du -h "$out/$name-signed.ipa" | cut -f1))"
    fi
    exit 0
fi

echo "Building with Xcode ($(xcodebuild -version | tr '\n' ' '))..."
xcodebuild -project "$out/$name.xcodeproj" -scheme "$name" -sdk iphoneos -configuration Release \
    -destination "generic/platform=iOS" -derivedDataPath "$out/xcode" build \
    CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO CODE_SIGN_IDENTITY="" > "$out/logs/xcodebuild.log" 2>&1 \
    || { tail -n 80 "$out/logs/xcodebuild.log"; echo "The Xcode build failed." >&2; exit 1; }

app="$out/xcode/Build/Products/Release-iphoneos/$name.app"
[ -d "$app" ] || { echo "Xcode didn't make $app." >&2; exit 1; }
rm -rf "$out/Payload" "$out/$name-unsigned.ipa"
mkdir "$out/Payload"
cp -R "$app" "$out/Payload/"
(cd "$out" && zip -qry "$name-unsigned.ipa" Payload && rm -rf Payload)

echo "Made $out/$name-unsigned.ipa ($(du -h "$out/$name-unsigned.ipa" | cut -f1)); the app itself is $(du -sh "$app" | cut -f1):"
du -sh "$app"/* | sort -rh | head -n 8
