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
"$root/scripts/package.sh" "$rid" "$publish" "$target"
