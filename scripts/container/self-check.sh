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

step "Yar installs and removes a Flatpak app itself, with undo"
app=com.github.tchx84.Flatseal
data="$HOME/.var/app/$app"
has() { flatpak list --user --app --columns=application | grep -qx "$app"; }
last() { yar-apps history | awk '$5 == "RemoveApp" { id = $1 } END { print id }'; }
yar-apps install "$app" --kind flatpak >/tmp/check.log 2>&1; grep -q 'Nothing was changed' /tmp/check.log && ! has \
  && pass "without --yes Yar only shows the plan" || fail "without --yes Yar only shows the plan"
sed 's/^/        /' /tmp/check.log
yar-apps install "$app" --kind flatpak --yes >/tmp/install.log 2>&1 && pass "Yar installs Flatseal for this user" || { fail "Yar installs Flatseal for this user"; tail -5 /tmp/install.log; }
echo "        progress lines Yar reported: $(grep -c '^  ' /tmp/install.log)"; grep '^  ' /tmp/install.log | sed -n '1,4p;$p' | sed 's/^/      /'
has && pass "flatpak agrees it is installed" || fail "flatpak agrees it is installed"
lists flatseal && pass "Yar lists Flatseal" || fail "Yar lists Flatseal"
mkdir -p "$data/config" && echo mine > "$data/config/settings"
check "Yar removes it and keeps its data" yar-apps remove "$app" --yes
! has && [ -f "$data/config/settings" ] && pass "the app is gone, the data is still there" || fail "the app is gone, the data is still there"
check "Yar undoes the removal" yar-apps undo "$(last)" --yes
has && pass "the app is back" || fail "the app is back"
check "Yar removes it together with its data" yar-apps remove "$app" --data --yes
! has && [ ! -e "$data" ] && [ -f "$HOME/.local/share/Trash/files/$app/config/settings" ] \
  && pass "the data is in the Trash, not erased" || fail "the data is in the Trash, not erased"
check "Yar undoes that too" yar-apps undo "$(last)" --yes
has && [ "$(cat "$data/config/settings" 2>/dev/null)" = mine ] && pass "app and data are both back" || fail "app and data are both back"
check "Yar undoes the very first install" yar-apps undo "$(yar-apps history | awk '$5 == "InstallApp" && $6 == "undo" { print $1; exit }')" --yes
! has && pass "the app is gone again" || fail "the app is gone again"
yar-apps install org.example.NoSuchApp --yes >/tmp/check.log 2>&1 && fail "an unknown app is refused" || pass "an unknown app is refused"
yar-apps history | sed 's/^/        /'
flatpak uninstall --user --noninteractive --unused >/dev/null 2>&1

check "Yar removes the source" yar-apps source-remove user:flathub

step "Yar installs and removes a system package itself, with undo"
# Any small app the distribution's catalog lists will do; which ones it lists differs between distributions.
pkg_app=""
for candidate in galculator.desktop org.xfce.mousepad com.github.tchx84.Flatseal org.gnome.Calculator; do
  if yar-apps install "$candidate" --kind packagekit 2>/dev/null | grep -q 'Nothing was changed'; then pkg_app="$candidate"; break; fi
done
if [ -z "$pkg_app" ]; then
  fail "the catalog lists an app that comes as a system package"
else
  yar-apps install "$pkg_app" --kind packagekit 2>&1 | sed 's/^/        /' | head -24
  yar-apps install "$pkg_app" --kind packagekit --yes >/tmp/install.log 2>&1 && pass "Yar installs $pkg_app as a system package" || { fail "Yar installs $pkg_app as a system package"; tail -5 /tmp/install.log; }
  echo "        progress Yar reported:"; grep -E '^  (Waiting|Downloading|Checking|Installing|Finishing)' /tmp/install.log | sed -n '1,8p;$p' | sed 's/^/      /'
  pkg_target="$(awk '/^done \(/ { print $3 }' /tmp/install.log)"
  pkg_name="${pkg_target%.*}"
  if command -v rpm >/dev/null; then have() { rpm -q "$pkg_name" >/dev/null 2>&1; }; else have() { dpkg -s "$pkg_name" 2>/dev/null | grep -q '^Status: install ok installed'; }; fi
  [ -n "$pkg_name" ] && have && pass "the package manager agrees $pkg_name is installed" || fail "the package manager agrees $pkg_name is installed"
  yar-apps apps --json 2>/dev/null | grep -q "\"$pkg_target\"" && pass "Yar lists it" || fail "Yar lists it"
  yar-apps remove "$pkg_target" 2>&1 | sed 's/^/        /' | head -14
  check "Yar removes it" yar-apps remove "$pkg_target" --yes
  ! have && pass "the package manager agrees it is gone" || fail "the package manager agrees it is gone"
  check "Yar undoes the removal" yar-apps undo "$(last)" --yes
  have && pass "it is back" || fail "it is back"
  check "Yar removes it again to leave things as they were" yar-apps remove "$pkg_target" --yes
  ! have && pass "it is gone again" || fail "it is gone again"
fi

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
