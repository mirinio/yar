#!/usr/bin/env bash
# Installs Yar for the current user only: the program, a "yar" command, a menu entry and an icon.
# Nothing outside the chosen prefix is touched and no password is needed.
#
#   scripts/install-local.sh                 install under ~/.local
#   scripts/install-local.sh --prefix DIR    install under DIR instead (for trying it out)
#
# scripts/uninstall-local.sh removes exactly what this puts there.
set -euo pipefail

cd "$(dirname "$0")/.."
prefix="$HOME/.local"
if [ "${1:-}" = "--prefix" ]; then prefix="$(realpath -m "$2")"; fi

id=org.example.Yar
dist="artifacts/dist/yar-linux-x64"
if [ ! -x "$dist/yar" ]; then
  echo "Building Yar..."
  scripts/publish.sh >/dev/null
fi

program="$prefix/opt/yar"
command="$prefix/bin/yar"
entry="$prefix/share/applications/$id.desktop"
metainfo="$prefix/share/metainfo/$id.metainfo.xml"
icon="$prefix/share/icons/hicolor/scalable/apps/$id.svg"

rm -rf "$program"
mkdir -p "$program" "$(dirname "$command")" "$(dirname "$entry")" "$(dirname "$metainfo")" "$(dirname "$icon")"
cp -r "$dist/." "$program/"

printf '#!/bin/sh\nexec "%s/yar" "$@"\n' "$program" > "$command"
chmod +x "$command"

# The menu entry starts Yar by its full path, so it works even when ~/.local/bin is not on the PATH.
sed "s|^Exec=yar\$|Exec=$command|" "data/$id.desktop" > "$entry"
cp "data/$id.metainfo.xml" "$metainfo"
cp assets/cube_logo.svg "$icon"

command -v update-desktop-database >/dev/null && update-desktop-database -q "$prefix/share/applications" || true

echo "Installed Yar:"
printf '  %s\n' "$program" "$command" "$entry" "$metainfo" "$icon"
echo "It should now be in your app menu. To remove it again: scripts/uninstall-local.sh"
