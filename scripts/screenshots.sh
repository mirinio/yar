#!/usr/bin/env bash
# Makes the pictures in docs/screenshots and docs/demo.gif from clean demo data.
#
# Yar runs inside the throwaway test container, where a few apps are installed for real and an
# app is added from a file, so the pictures show nobody's own computer. It draws into a hidden
# compositor on this machine; nothing appears on the screen. Needs podman, kwin_wayland and
# ImageMagick.
#
#   scripts/screenshots.sh            pictures in both themes, and the animation
set -euo pipefail

cd "$(dirname "$0")/.."
out="docs/screenshots"
work="artifacts/screenshots"
socket="yar-screenshots"
mkdir -p "$out" "$work/frames" "$work/demo"

echo "Building Yar with its screenshot mode, and the command-line tool..."
dotnet publish src/Presentation/Yar.Gtk -c Screenshots -r linux-x64 --self-contained -o artifacts/screenshots-build -v q >/dev/null
scripts/publish.sh >/dev/null

# A made-up app to add from a file: an archive with a program, a launcher and a picture.
demo="$work/demo/pixel-paint-1.4.0"
rm -rf "$demo" && mkdir -p "$demo/bin"
cp /usr/bin/true "$demo/bin/pixel-paint"
magick -size 256x256 xc:none -fill '#5b8def' -draw 'roundrectangle 16,16 240,240 48,48' \
  -fill '#ffd166' -draw 'circle 100,110 100,60' -fill '#ef476f' -draw 'circle 160,150 160,105' \
  -fill '#06d6a0' -draw 'circle 110,175 110,140' "$demo/pixel-paint.png"
printf '[Desktop Entry]\nType=Application\nName=Pixel Paint\nComment=Draw pictures, one pixel at a time\nExec=pixel-paint\nIcon=pixel-paint\nCategories=Graphics;\n' \
  > "$demo/pixel-paint.desktop"
tar -C "$work/demo" -czf "$work/demo/pixel-paint-1.4.0-linux-x86_64.tar.gz" pixel-paint-1.4.0

kwin_wayland --virtual --socket "$socket" --width 1100 --height 900 --no-lockscreen >"$work/kwin.log" 2>&1 &
kwin=$!
trap 'kill "$kwin" 2>/dev/null; wait "$kwin" 2>/dev/null' EXIT
for _ in $(seq 1 40); do [ -S "$XDG_RUNTIME_DIR/$socket" ] && break; read -rt 0.25 < /dev/zero || true; done

release="$(. /etc/os-release 2>/dev/null && [ "${ID:-}" = fedora ] && echo "${VERSION_ID}" || echo 44)"
podman run --rm --security-opt label=disable \
  -v "$PWD/artifacts/dist/yar-linux-x64:/yar:ro" \
  -v "$PWD/artifacts/screenshots-build:/gui:ro" \
  -v "$PWD/scripts/container:/setup:ro" \
  -v "$PWD/$work:/work" \
  -v "$XDG_RUNTIME_DIR/$socket:/run/wl/$socket" \
  -e YAR_TEST_MODE=command -e WAYLAND_SOCKET_PATH="/run/wl/$socket" \
  "registry.fedoraproject.org/fedora:$release" /setup/entry.sh /setup/screenshots-inside.sh

echo "Finishing the pictures..."
for picture in "$work"/shots/*.png; do
  magick "$picture" -strip -define png:compression-level=9 "$out/$(basename "$picture")"
done

# The animation: a slideshow, each frame stored once and held for a moment, a little smaller.
magick -delay 240 -loop 0 "$work"/frames/*.png -resize 820x -dither FloydSteinberg -colors 160 -layers Optimize docs/demo.gif

# The picture shown when the repository's link is shared: logo, name and one screenshot.
magick -size 1280x640 gradient:'#1e2a44-#241f31' \
  \( -background none assets/cube_logo.svg -resize 150x150 \) -geometry +80+120 -composite \
  -font Noto-Sans-Bold -fill white -pointsize 92 -annotate +80+380 'Yar' \
  -font Noto-Sans-Regular -fill '#c8d0e0' -pointsize 34 -annotate +84+440 'An app manager for Linux' \
  -pointsize 21 -fill '#9aa4b8' -annotate +84+500 'Every installed app, from any source, in one place.' \
  \( "$out/store-app-dark.png" -resize 620x \( +clone -background black -shadow 60x14+0+10 \) +swap -background none -layers merge +repage \) \
  -geometry +610+110 -composite -strip docs/social-preview.png

ls -la "$out" docs/demo.gif docs/social-preview.png
