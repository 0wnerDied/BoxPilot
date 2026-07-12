#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <osx-arm64|osx-x64|win-x64|win-arm64>" >&2
  exit 2
fi

rid="$1"
case "$rid" in
  osx-arm64|osx-x64|win-x64|win-arm64) ;;
  *)
    echo "Unsupported runtime identifier: $rid" >&2
    exit 2
    ;;
esac

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$root/src/BoxPilot.App/BoxPilot.App.csproj"
target="$root/dist/$rid"
publish="$target/publish"

rm -rf "$target"
mkdir -p "$publish"

dotnet restore "$project" -r "$rid"
dotnet publish "$project" \
  -c Release \
  -r "$rid" \
  --self-contained true \
  --no-restore \
  -o "$publish" \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PublishTrimmed=false \
  -p:DebugType=None \
  -p:DebugSymbols=false

find "$publish" -type f -name '*.pdb' -delete

if [[ "$rid" == osx-* ]]; then
  app="$target/BoxPilot.app"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
  cp -R "$publish/". "$app/Contents/MacOS/"
  rm -rf "$publish"
  cp "$root/src/BoxPilot.App/Assets/boxpilot.icns" "$app/Contents/Resources/boxpilot.icns"
  cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDisplayName</key><string>BoxPilot</string>
  <key>CFBundleExecutable</key><string>BoxPilot</string>
  <key>CFBundleIconFile</key><string>boxpilot</string>
  <key>CFBundleIdentifier</key><string>tech.0b1t.boxpilot</string>
  <key>CFBundleName</key><string>BoxPilot</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST
  chmod +x "$app/Contents/MacOS/BoxPilot"
  if command -v codesign >/dev/null 2>&1; then
    codesign --force --deep --sign - "$app"
  fi
  ditto -c -k --sequesterRsrc --keepParent "$app" "$target/BoxPilot-$rid.zip"
else
  package="$target/BoxPilot"
  mv "$publish" "$package"
  (
    cd "$target"
    zip -qr "BoxPilot-$rid.zip" BoxPilot
  )
fi

echo "Created $target"
