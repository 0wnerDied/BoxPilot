#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "Usage: $0 <RID> <publish-directory> <target-directory>" >&2
  exit 2
fi

rid="$1"
publish="$2"
target="$3"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

case "$rid" in
  osx-arm64|osx-x64|win-x64|win-arm64) ;;
  *)
    echo "Unsupported runtime identifier: $rid" >&2
    exit 2
    ;;
esac

if [[ ! -d "$publish" ]]; then
  echo "Publish directory does not exist: $publish" >&2
  exit 1
fi

mkdir -p "$target"

if [[ "$rid" == osx-* ]]; then
  if ! command -v hdiutil >/dev/null 2>&1; then
    echo "hdiutil is required to create a macOS DMG" >&2
    exit 1
  fi

  staging="$target/dmg-root"
  app="$staging/BoxPilot.app"
  dmg="$target/BoxPilot-$rid.dmg"

  rm -rf "$staging" "$dmg"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
  cp -R "$publish/". "$app/Contents/MacOS/"
  rm -rf "$publish"
  cp "$root/src/BoxPilot.App/Assets/boxpilot.icns" \
    "$app/Contents/Resources/boxpilot.icns"
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

  ln -s /Applications "$staging/Applications"
  sync
  created=false
  for attempt in 1 2 3; do
    rm -f "$dmg"
    if hdiutil create \
      -volname BoxPilot \
      -srcfolder "$staging" \
      -format UDZO \
      -ov \
      "$dmg"; then
      created=true
      break
    fi

    if [[ "$attempt" -lt 3 ]]; then
      sleep 1
    fi
  done
  if [[ "$created" != true ]]; then
    echo "Could not create macOS DMG after 3 attempts" >&2
    exit 1
  fi
  hdiutil verify "$dmg" >/dev/null
  rm -rf "$staging"

  echo "Created $dmg"
  exit 0
fi

if [[ ! -f "$publish/BoxPilot.exe" ]]; then
  echo "Windows publish is missing BoxPilot.exe" >&2
  exit 1
fi

if find "$publish" -type f -iname '*.dll' -print -quit | grep -q .; then
  if ! command -v zip >/dev/null 2>&1; then
    echo "zip is required when Windows DLL dependencies are present" >&2
    exit 1
  fi

  package="$target/BoxPilot"
  archive="$target/BoxPilot-$rid.zip"
  rm -rf "$package" "$archive"
  mv "$publish" "$package"
  (
    cd "$target"
    zip -qr "$(basename "$archive")" BoxPilot
  )
  echo "Created $archive"
  exit 0
fi

cp -R "$publish/". "$target/"
rm -rf "$publish"
echo "Created $target/BoxPilot.exe"
