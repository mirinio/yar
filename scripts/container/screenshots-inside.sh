#!/bin/bash
# Runs inside the test container for scripts/screenshots.sh: sets up a small, clean demo
# computer through Yar itself, then lets Yar's screenshot mode take the pictures.
set -uo pipefail

step() { printf '\n== %s\n' "$*"; }
quiet() { "$@" >/tmp/last.log 2>&1 || { echo "  failed: $*"; tail -3 /tmp/last.log; }; }

step "What the app window needs"
quiet dnf install -y -q gtk4 libadwaita google-noto-sans-fonts google-noto-sans-mono-fonts adwaita-icon-theme \
  adwaita-cursor-theme breeze-icon-theme squashfs-tools xterm

# A home folder with a plain name, so that paths in the pictures read naturally.
export HOME=/home/demo XDG_RUNTIME_DIR=/tmp/runtime
# As a desktop session would have it: Flatpak apps' icons and menu entries are found here.
export XDG_DATA_DIRS="$HOME/.local/share/flatpak/exports/share:/var/lib/flatpak/exports/share:/usr/local/share:/usr/share"
mkdir -p "$HOME" "$XDG_RUNTIME_DIR" && chmod 700 "$XDG_RUNTIME_DIR"
ln -sf "$WAYLAND_SOCKET_PATH" "$XDG_RUNTIME_DIR/wayland-yar"
cd "$HOME"
# The icon theme KDE Plasma uses; the default one lacks some of the small icons Yar asks for.
mkdir -p "$HOME/.config/gtk-4.0"
printf '[Settings]\ngtk-icon-theme-name=breeze\n' > "$HOME/.config/gtk-4.0/settings.ini"

step "A few apps, installed through Yar"
quiet yar-apps source-add https://dl.flathub.org/repo/flathub.flatpakrepo flathub
quiet yar-apps install com.github.tchx84.Flatseal --kind flatpak --yes
quiet yar-apps install org.gnome.Calculator --kind flatpak --yes
quiet yar-apps install org.xfce.mousepad --kind packagekit --yes
quiet yar-apps install galculator.desktop --kind packagekit --yes
hash="$(sha256sum /work/demo/pixel-paint-1.4.0-linux-x86_64.tar.gz | cut -d' ' -f1)"
quiet yar-apps add /work/demo/pixel-paint-1.4.0-linux-x86_64.tar.gz --hash "$hash" --yes
quiet yar-apps remove galculator.x86_64 --yes

step "Updates waiting, so the Updates tab has something to show"
older="$(flatpak remote-info --user --log flathub com.github.tchx84.Flatseal 2>/dev/null | awk '/Commit:/ { print $2 }' | sed -n 2p)"
[ -n "$older" ] && quiet flatpak update --user --noninteractive --commit="$older" com.github.tchx84.Flatseal
for candidate in tzdata less sed gawk grep gzip; do dnf -y -q downgrade "$candidate" >/dev/null 2>&1 && break; done
pkcon refresh >/dev/null 2>&1
yar-apps apps | tail -3

shoot() { # shoot <theme> <steps>
  # no-portals: otherwise GTK takes its icon theme from the container's settings service, not from settings.ini.
  WAYLAND_DISPLAY=wayland-yar GDK_BACKEND=wayland GDK_DEBUG=no-portals GSK_RENDERER=cairo YAR_THEME="$1" YAR_SHOT_SIZE=1040x720 \
    YAR_SHOT_DIR="$2" YAR_SHOT_STEPS="$3" timeout 120 dbus-run-session -- /gui/yar >>/work/app.log 2>&1 \
    || echo "  the app ended with an error; see artifacts/screenshots/app.log"
}

step "Pictures"
mkdir -p /work/shots /work/frames
rm -f /work/shots/*.png /work/frames/*.png /work/app.log
krita="org.kde.krita"
for theme in dark light; do
  echo "  $theme"
  shoot "$theme" /work/shots "section:apps;wait:5000;shot:my-apps-$theme.png"
  shoot "$theme" /work/shots "section:store;wait:12000;shot:store-$theme.png"
  shoot "$theme" /work/shots "section:store;wait:3000;store:$krita;wait:9000;shot:store-app-$theme.png"
  shoot "$theme" /work/shots "section:store;wait:3000;store:$krita;wait:3000;install:$krita:flatpak;wait:10000;shot:install-preview-$theme.png"
  shoot "$theme" /work/shots "section:apps;wait:3000;add:/work/demo/pixel-paint-1.4.0-linux-x86_64.tar.gz;wait:6000;shot:add-app-$theme.png"
  shoot "$theme" /work/shots "section:history;wait:3000;shot:history-$theme.png"
  # No Storage picture: inside a container the disks it would show are the real computer's.
  shoot "$theme" /work/shots "section:updates;wait:5000;shot:updates-$theme.png"
done

step "Frames for the animation"
shoot dark /work/frames "section:store;wait:12000;shot:01.png;store:$krita;wait:8000;shot:02.png;install:$krita:flatpak;wait:10000;shot:03.png"
shoot dark /work/frames "section:apps;wait:4000;installnow:org.gnome.Characters:flatpak;wait:3000;shot:04.png;wait:5000;shot:05.png;wait:60000;section:history;wait:3000;shot:06.png"
shoot dark /work/frames "section:apps;wait:3000;add:/work/demo/pixel-paint-1.4.0-linux-x86_64.tar.gz;wait:6000;shot:07.png"
ls /work/shots /work/frames
