#!/bin/bash
set -euo pipefail

DEPLOY_DIR="/opt/nightshift"
PROJECT="src/Nightshift.Engine/Nightshift.Engine.csproj"
REPO_ROOT="$(cd "$(dirname "$0")" && pwd)"

# Get git SHA for version stamp
SHA=$(git -C "$REPO_ROOT" rev-parse --short HEAD)
TAG=$(git -C "$REPO_ROOT" describe --tags --exact-match 2>/dev/null || echo "")

if [ -n "$TAG" ]; then
    VERSION_SUFFIX="$TAG+$SHA"
else
    VERSION_SUFFIX="0.0.0-dev+$SHA"
fi

echo "Publishing Nightshift engine: $VERSION_SUFFIX"

# Build release
dotnet publish "$REPO_ROOT/$PROJECT" \
    -c Release \
    -o "$DEPLOY_DIR" \
    -p:InformationalVersion="$VERSION_SUFFIX"

# Copy appsettings (not included in publish by default unless configured)
cp "$REPO_ROOT/src/Nightshift.Engine/appsettings.json" "$DEPLOY_DIR/" 2>/dev/null || true

echo ""
echo "Published to: $DEPLOY_DIR"
echo "Version: $VERSION_SUFFIX"
echo ""
echo "Run with:"
echo "  cd $DEPLOY_DIR && dotnet Nightshift.Engine.dll"
