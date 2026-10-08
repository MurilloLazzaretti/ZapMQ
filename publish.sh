#!/usr/bin/env bash
# Builds the panel interface and then the service with it inside.
# The result is in publish/win-x64: ZapMQ.exe and appsettings.json.
set -euo pipefail
cd "$(dirname "$0")"

(cd src/ZapMQ.Panel && npm ci && npm run build)

rm -rf publish/win-x64
dotnet publish src/ZapMQ.Server -c Release -r win-x64 -o publish/win-x64

echo "Published to publish/win-x64"
