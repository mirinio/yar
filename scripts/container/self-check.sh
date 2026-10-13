#!/bin/bash
# Proves the test container can do what install and uninstall work will need:
# real installs and removals through the system's own tools, and Yar seeing each change.
set -uo pipefail

failures=0
pass() { printf '  ok    %s\n' "$*"; }
fail() { printf '  FAIL  %s\n' "$*"; failures=$((failures + 1)); }
check() { local what="$1"; shift; if "$@" >/tmp/check.log 2>&1; then pass "$what"; else fail "$what"; sed 's/^/        /' /tmp/check.log | tail -5; fi; }
step() { printf '\n== %s\n' "$*"; }
lists() { yar-apps apps 2>/dev/null | grep -qi "$1"; }

step "Yar sees the backends"
status="$(yar-apps status 2>&1)"; echo "$status" | sed 's/^/  /'
echo "$status" | grep -q '^packagekit *Available' && pass "PackageKit is available" || fail "PackageKit is available"
echo "$status" | grep -q '^flatpak *Available' && pass "Flatpak is available" || fail "Flatpak is available"

# PackageKit goes first: installing with another tool afterwards leaves its daemon a step behind,
# which is exactly what the section after this one is there to test.
step "PackageKit accepts install and remove requests here, and Yar notices"
if command -v pkcon >/dev/null; then
  lists xterm && fail "xterm is not there before the test" || pass "xterm is not there before the test"
  check "pkcon installs xterm" pkcon install -y xterm
  lists xterm && pass "Yar lists XTerm after PackageKit installed it" || fail "Yar lists XTerm after PackageKit installed it"
  check "pkcon removes xterm" pkcon remove -y xterm
  lists xterm && fail "Yar no longer lists XTerm" || pass "Yar no longer lists XTerm"
else
  fail "pkcon is not installed, so PackageKit installs could not be tried"
fi

step "A package installed behind PackageKit's back is still noticed by Yar"
if command -v dnf >/dev/null; then install="dnf install -y -q"; remove="dnf remove -y -q"; else install="apt-get install -y -qq"; remove="apt-get remove -y -qq"; fi
check "the system's own tool installs xterm" $install xterm
lists xterm && pass "Yar lists XTerm after the install" || fail "Yar lists XTerm after the install"
check "the system's own tool removes xterm" $remove xterm
lists xterm && fail "Yar no longer lists XTerm" || pass "Yar no longer lists XTerm"

step "Flatpak sources through Yar"
check "Yar adds Flathub for this user" yar-apps source-add https://dl.flathub.org/repo/flathub.flatpakrepo flathub
flatpak remotes --user --columns=name | grep -qx flathub && pass "flatpak agrees the source is there" || fail "flatpak agrees the source is there"
check "Yar switches the source off" yar-apps source-switch flatpak user:flathub off
flatpak remotes --user --show-disabled --columns=name,options | grep -q 'flathub.*disabled' && pass "flatpak shows it as disabled" || fail "flatpak shows it as disabled"
check "Yar switches the source on again" yar-apps source-switch flatpak user:flathub on

step "A Flatpak app can be installed for this user and removed, and Yar notices"
# Without its runtime the app cannot run, but it is installed, which is all this needs. It keeps the download small.
check "flatpak installs Flatseal (app only)" flatpak install --user --noninteractive --no-deps flathub com.github.tchx84.Flatseal
lists flatseal && pass "Yar lists Flatseal" || fail "Yar lists Flatseal"
yar-apps source-remove user:flathub >/tmp/check.log 2>&1 && fail "Yar refuses to remove a source that still has apps" || pass "Yar refuses to remove a source that still has apps"
check "flatpak removes Flatseal" flatpak uninstall --user --noninteractive com.github.tchx84.Flatseal
lists flatseal && fail "Yar no longer lists Flatseal" || pass "Yar no longer lists Flatseal"
check "Yar removes the source" yar-apps source-remove user:flathub

step "A package source can be switched through Yar (the switch that needs a password on a real system)"
source_id="$(yar-apps sources 2>/dev/null | awk '$1 == "packagekit" && $3 == "off" { print $2; exit }')"
if [ -n "$source_id" ]; then
  check "Yar switches $source_id on" yar-apps source-switch packagekit "$source_id" on
  yar-apps sources 2>/dev/null | awk -v id="$source_id" '$2 == id { print $3 }' | grep -qx on && pass "it now reads as on" || fail "it now reads as on"
  check "Yar switches $source_id off again" yar-apps source-switch packagekit "$source_id" off
  yar-apps sources 2>/dev/null | awk -v id="$source_id" '$2 == id { print $3 }' | grep -qx off && pass "it reads as off again" || fail "it reads as off again"
else
  echo "  skip  no switched-off package source that Yar may switch on this distribution"
fi

printf '\n'
if [ "$failures" -eq 0 ]; then echo "Self-check passed."; else echo "Self-check: $failures step(s) failed."; fi
exit "$failures"
