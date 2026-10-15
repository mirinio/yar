#!/usr/bin/env bash
# Removes what scripts/install-local.sh installed, and nothing else.
# Settings, saved lists, the history and apps you added with Yar are left alone; it says where they are.
#
#   scripts/uninstall-local.sh                 remove from ~/.local
#   scripts/uninstall-local.sh --prefix DIR    remove from DIR instead
set -euo pipefail

prefix="$HOME/.local"
if [ "${1:-}" = "--prefix" ]; then prefix="$(realpath -m "$2")"; fi
id=io.github.mirinio.Yar

program="$prefix/opt/yar"
# Only remove the program folder if it really is a Yar installation.
if [ -d "$program" ] && [ ! -e "$program/yar" ]; then
  echo "$program does not look like an installation of Yar; leaving it alone." >&2
  exit 1
fi

removed=0
for path in \
  "$program" \
  "$prefix/bin/yar" \
  "$prefix/share/applications/$id.desktop" \
  "$prefix/share/metainfo/$id.metainfo.xml" \
  "$prefix/share/icons/hicolor/scalable/apps/$id.svg"
do
  if [ -e "$path" ]; then
    rm -rf -- "$path"
    echo "  removed $path"
    removed=$((removed + 1))
  fi
done

command -v update-desktop-database >/dev/null && [ -d "$prefix/share/applications" ] \
  && update-desktop-database -q "$prefix/share/applications" || true

if [ "$removed" -eq 0 ]; then echo "Yar was not installed under $prefix."; else echo "Yar is removed."; fi
echo "Left alone: your settings (~/.config/yar), saved lists (~/.cache/yar), the history of changes"
echo "(~/.local/state/yar), and the apps you added with Yar, with their notes in ~/.local/share/yar."
