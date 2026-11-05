#!/usr/bin/env bash
# Build SimpleDream.app and a DMG for macOS. Usage: scripts/make-dmg.sh [rid] (default osx-arm64)
# Output: artifacts/mac/SimpleDream.app and artifacts/SimpleDream-<version>-<arch>.dmg
# Self-contained, ad-hoc signed, not notarized.
set -euo pipefail
cd "$(dirname "$0")/.."
# Homebrew's dotnet bundles a runtime linked to Homebrew's brotli, which wouldn't run on
# other Macs. Use Microsoft's when it's installed.
[ -x /usr/local/share/dotnet/dotnet ] && export PATH="/usr/local/share/dotnet:$PATH"
export MACOSX_DEPLOYMENT_TARGET=14.0
rid="${1:-osx-arm64}"
arch="${rid#osx-}"
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/SimpleDream/SimpleDream.csproj)
out=artifacts/mac
app="$out/SimpleDream.app"
stage="$out/stage"

rm -rf "$out"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$stage"

echo "== app ($rid)"
touch native/src/lib.rs   # so the deployment target above reaches the library
dotnet publish src/SimpleDream/SimpleDream.csproj -c Release -r "$rid" --self-contained true \
  -p:UseAppHost=true -o "$app/Contents/MacOS"
find "$app/Contents" -name '*.pdb' -delete

cp src/SimpleDream/Assets/AppIcon.icns "$app/Contents/Resources/"
cp LICENSE "$app/Contents/Resources/"

cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>SimpleDream $version</string>
  <key>CFBundleDisplayName</key><string>SimpleDream</string>
  <key>CFBundleIdentifier</key><string>com.kc2rgw.simpledream</string>
  <key>CFBundleExecutable</key><string>SimpleDream</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSLocalNetworkUsageDescription</key>
  <string>SimpleDream connects to your radio over TCI on the local network.</string>
</dict>
</plist>
EOF

echo "== check"
scripts/check-mac-bundle.sh "$app"

echo "== sign (ad-hoc)"
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"

echo "== dmg"
ditto "$app" "$stage/SimpleDream.app"
ln -s /Applications "$stage/Applications"
dmg="artifacts/SimpleDream-$version-$arch.dmg"
rm -f "$dmg"
hdiutil create -volname "SimpleDream $version" -srcfolder "$stage" -format UDZO -ov "$dmg" >/dev/null
rm -rf "$stage"
du -sh "$app" "$dmg"
echo "dmg -> $dmg"
