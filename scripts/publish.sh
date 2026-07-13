#!/usr/bin/env bash
set -euo pipefail

create_macos_dmg() {
  local rid="$1"
  local root="$2"
  local publish="$3"
  local target="$4"
  local staging="$target/dmg-root"
  local app="$staging/BoxPilot.app"
  local dmg="$target/BoxPilot-$rid.dmg"
  local created=false

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
  <key>CFBundleShortVersionString</key><string>1.0.1</string>
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
  cp "$root/docs/macos/README.txt" "$staging/README.txt"

  # DiskImages can race recent signature metadata writes, so retry briefly.
  sync
  for attempt in 1 2 3; do
    rm -f "$dmg"
    if hdiutil create \
      -volname BoxPilot \
      -srcfolder "$staging" \
      -format UDZO \
      -imagekey zlib-level=9 \
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
    return 1
  fi

  hdiutil verify "$dmg" >/dev/null
  rm -rf "$staging"
  echo "Created $dmg"
}

create_windows_package() {
  local rid="$1"
  local publish="$2"
  local target="$3"

  if [[ ! -f "$publish/BoxPilot.exe" ]]; then
    echo "Windows publish is missing BoxPilot.exe" >&2
    return 1
  fi

  if find "$publish" -type f -iname '*.dll' -print -quit | grep -q .; then
    if ! command -v zip >/dev/null 2>&1; then
      echo "zip is required when Windows DLL dependencies are present" >&2
      return 1
    fi

    local package="$target/BoxPilot"
    local archive="$target/BoxPilot-$rid.zip"
    mv "$publish" "$package"
    (
      cd "$target"
      zip -qr "$(basename "$archive")" BoxPilot
    )
    echo "Created $archive"
    return
  fi

  cp -R "$publish/". "$target/"
  rm -rf "$publish"
  echo "Created $target/BoxPilot.exe"
}

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

if [[ "$rid" == osx-* ]] && ! command -v hdiutil >/dev/null 2>&1; then
  echo "hdiutil is required to create a macOS DMG" >&2
  exit 1
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$root/src/BoxPilot.App/BoxPilot.App.csproj"
target="$root/dist/$rid"
publish="$target/publish"

rm -rf "$target"
mkdir -p "$publish"

dotnet restore "$project" -r "$rid" -p:Configuration=Release
dotnet publish "$project" \
  -c Release \
  -r "$rid" \
  --self-contained true \
  --no-restore \
  -o "$publish"

find "$publish" -type f -name '*.pdb' -delete

if [[ "$rid" == osx-* ]]; then
  create_macos_dmg "$rid" "$root" "$publish" "$target"
else
  create_windows_package "$rid" "$publish" "$target"
fi
