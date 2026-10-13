#!/usr/bin/env bash
# Starts a throwaway container in which Yar's command-line tool can install and remove
# software for real, so that code which changes a system can be tested without touching yours.
#
# Inside the container everything runs as root. That is deliberate and only acceptable here:
# as root, PackageKit accepts install and remove requests without a password prompt, which is
# the one way to exercise that path automatically. Yar itself never runs as root on a real
# system. The container is deleted when it exits; nothing in it is kept.
#
#   scripts/test-container.sh                     a shell in a Fedora container
#   scripts/test-container.sh --self-check        prove the rig works, then exit
#   scripts/test-container.sh --distro ubuntu     the same on Ubuntu 24.04
#   scripts/test-container.sh -- yar-apps apps    run one command
set -euo pipefail

cd "$(dirname "$0")/.."
distro=fedora
mode=shell
rebuild=no
command=()
while [ $# -gt 0 ]; do
  case "$1" in
    --distro) distro="$2"; shift 2 ;;
    --self-check) mode=self-check; shift ;;
    --rebuild) rebuild=yes; shift ;;
    --) shift; command=("$@"); mode=command; break ;;
    -h|--help) sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

case "$distro" in
  fedora)
    release="$(. /etc/os-release 2>/dev/null && [ "${ID:-}" = fedora ] && echo "${VERSION_ID}" || echo 44)"
    image="registry.fedoraproject.org/fedora:${release}" ;;
  ubuntu) image="docker.io/library/ubuntu:24.04" ;;
  *) echo "Unknown distro: $distro (fedora or ubuntu)" >&2; exit 2 ;;
esac

dist="artifacts/dist/yar-linux-x64"
if [ "$rebuild" = yes ] || [ ! -x "$dist/yar-apps" ]; then
  echo "Building Yar for the container..."
  scripts/publish.sh >/dev/null
fi

interactive=()
if [ "$mode" = shell ] && [ -t 0 ]; then
  interactive=(-it)
fi

exec podman run --rm "${interactive[@]}" \
  -v "$PWD/$dist:/yar:ro,z" \
  -v "$PWD/scripts/container:/setup:ro,z" \
  -e YAR_TEST_MODE="$mode" \
  "$image" /setup/entry.sh "${command[@]}"
