#!/usr/bin/env bash
# Once per Linux dev machine, rerun-safe (DEVELOPMENT.md section 1, "On Linux"): exactly the .NET
# SDK global.json names (in ~/.dotnet), the UI test runner's packages and the Chrome it drives.
# Needs Node.js and curl; Chrome's system libraries need root or sudo (playwright --with-deps).
set -euo pipefail
repo="$(cd "$(dirname "$0")/../../.." && pwd)"
sdk="$(node -p "require('$repo/global.json').sdk.version")"
dotnet_dir="$HOME/.dotnet"

if "$dotnet_dir/dotnet" --list-sdks 2>/dev/null | grep -q "^$sdk "; then
  echo ".NET SDK $sdk: already in $dotnet_dir"
else
  installer="$(mktemp)"
  curl -sSL https://dot.net/v1/dotnet-install.sh -o "$installer"
  bash "$installer" --version "$sdk" --install-dir "$dotnet_dir"
  rm -f "$installer"
fi

cd "$repo/launcher/dev/linux"
npm ci --no-audit --no-fund
npx playwright install --with-deps chrome

echo
echo "Ready. In each shell (or your profile):"
echo "  export PATH=\"$dotnet_dir:\$PATH\" DOTNET_CLI_TELEMETRY_OPTOUT=1"
echo "Then: dotnet build launcher/src/Launcher/Launcher.csproj -c Release; node launcher/dev/linux/test-ui.mjs"
