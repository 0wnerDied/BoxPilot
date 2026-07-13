#!/usr/bin/env bash
set -euo pipefail

create_macos_dmg() {
  local root="$1"
  local publish="$2"
  local target="$3"
  local app_name="$4"
  local version="$5"
  local artifact_name="$6"
  local staging="$target/dmg-root"
  local app="$staging/$app_name.app"
  local dmg="$target/$artifact_name.dmg"
  local created=false

  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
  cp -R "$publish/". "$app/Contents/MacOS/"
  rm -rf "$publish"
  cp "$root/src/BoxPilot.App/Assets/boxpilot.icns" \
    "$app/Contents/Resources/boxpilot.icns"
  cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDisplayName</key><string>$app_name</string>
  <key>CFBundleExecutable</key><string>$app_name</string>
  <key>CFBundleIconFile</key><string>boxpilot</string>
  <key>CFBundleIdentifier</key><string>tech.0b1t.boxpilot</string>
  <key>CFBundleName</key><string>$app_name</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST
  chmod +x "$app/Contents/MacOS/$app_name"
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
      -volname "$app_name" \
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
  local publish="$1"
  local target="$2"
  local app_name="$3"
  local artifact_name="$4"
  local executable="$publish/$app_name.exe"
  local artifact="$target/$artifact_name.exe"

  if [[ ! -f "$executable" ]]; then
    echo "Windows publish is missing $app_name.exe" >&2
    return 1
  fi

  if find "$publish" -type f -iname '*.dll' -print -quit | grep -q .; then
    echo "Windows publish contains DLL dependencies instead of one executable" >&2
    return 1
  fi

  mv "$executable" "$artifact"
  rm -rf "$publish"
  echo "Created $artifact"
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
app_name="$(dotnet msbuild "$project" -getProperty:AssemblyName -nologo | tr -d '\r')"
version="$(dotnet msbuild "$project" -getProperty:Version -nologo | tr -d '\r')"

if [[ -z "$app_name" || -z "$version" ]]; then
  echo "Could not read the application name or version from $project" >&2
  exit 1
fi

case "$rid" in
  osx-arm64)
    platform=macos
    architecture=arm64
    ;;
  osx-x64)
    platform=macos
    architecture=x64
    ;;
  win-x64)
    platform=windows
    architecture=x64
    ;;
  win-arm64)
    platform=windows
    architecture=arm64
    ;;
esac
artifact_name="$app_name-$platform-$architecture-$version"

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
  create_macos_dmg \
    "$root" "$publish" "$target" "$app_name" "$version" "$artifact_name"
else
  create_windows_package "$publish" "$target" "$app_name" "$artifact_name"
fi
