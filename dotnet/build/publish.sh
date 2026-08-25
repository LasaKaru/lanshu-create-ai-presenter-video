#!/usr/bin/env bash
# Publishes self-contained, single-file builds of the studio and the CLI.
#
#   ./build/publish.sh                 # win-x64, the download most people want
#   ./build/publish.sh linux-x64       # or osx-arm64, win-arm64, ...
#   ./build/publish.sh win-x64 ./out   # choose the output directory

set -euo pipefail

RID="${1:-win-x64}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
OUTPUT="${2:-$ROOT/artifacts/$RID}"

command -v dotnet >/dev/null 2>&1 || {
  printf 'ERROR: the .NET 8 SDK is required. See https://dotnet.microsoft.com/download\n' >&2
  exit 2
}

printf 'Publishing %s to %s\n' "$RID" "$OUTPUT"
rm -rf -- "$OUTPUT"

# EnableWindowsTargeting lets a Linux or macOS host produce the Windows build.
COMMON=(-c Release -r "$RID" --self-contained true
        -p:PublishSingleFile=true -p:EnableWindowsTargeting=true --nologo)

dotnet publish "$ROOT/src/Lanshu.Presenter.App/Lanshu.Presenter.App.csproj" "${COMMON[@]}" -o "$OUTPUT"
dotnet publish "$ROOT/src/Lanshu.Presenter.Cli/Lanshu.Presenter.Cli.csproj" "${COMMON[@]}" -o "$OUTPUT"

cp -- "$ROOT/../LICENSE" "$OUTPUT/LICENSE.txt" 2>/dev/null || true
cp -- "$ROOT/DOWNLOAD.md" "$OUTPUT/README.txt" 2>/dev/null || true

printf '\nPublished:\n'
ls -la "$OUTPUT"
