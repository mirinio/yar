#!/bin/bash
# Runs inside the test container: installs what Yar talks to, starts the message bus and
# PackageKit, then hands over to a shell, a command, or the self-check.
set -uo pipefail

export HOME=/root PATH="/yar:$PATH" XDG_DATA_DIRS=/usr/local/share:/usr/share LC_ALL=C.UTF-8

step() { printf '\n== %s\n' "$*"; }

step "Installing what Yar talks to"
if command -v dnf >/dev/null; then
  dnf install -y -q PackageKit flatpak appstream dbus-daemon libicu >/dev/null 2>&1 || { echo "dnf install failed"; exit 1; }
elif command -v apt-get >/dev/null; then
  export DEBIAN_FRONTEND=noninteractive
  # The stock container image switches off the downloads that carry the app catalog.
  rm -f /etc/apt/apt.conf.d/docker-gzip-indexes /etc/apt/apt.conf.d/docker-clean
  apt-get update -qq >/dev/null 2>&1
  apt-get install -y -qq --no-install-recommends libicu74 ca-certificates dbus packagekit packagekit-tools appstream flatpak >/dev/null 2>&1 \
    || { echo "apt install failed"; exit 1; }
  apt-get update -qq >/dev/null 2>&1
fi

step "Starting the system message bus"
mkdir -p /run/dbus
dbus-daemon --system --fork || { echo "could not start dbus-daemon"; exit 1; }

# Without systemd the bus cannot start PackageKit on demand on every distribution, so start it here.
daemon="$(command -v packagekitd || ls /usr/libexec/packagekitd /usr/lib/packagekit/packagekitd 2>/dev/null | head -1)"
if [ -n "$daemon" ] && ! busctl --system status org.freedesktop.PackageKit >/dev/null 2>&1; then
  "$daemon" >/var/log/packagekitd.log 2>&1 &
  sleep 2
fi

case "${YAR_TEST_MODE:-shell}" in
  self-check) exec /setup/self-check.sh ;;
  command) exec "$@" ;;
  *) echo; echo "Yar's tool is on the PATH as yar-apps. Everything here is thrown away on exit."; exec bash ;;
esac
