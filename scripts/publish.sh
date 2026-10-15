#!/usr/bin/env bash
# Builds a self-contained tarball of Yar that runs without .NET installed, ready to install
# with the install-local.sh inside it. GTK 4.14+ and libadwaita 1.5+ must be present on the
# computer that runs it.
#
#   scripts/publish.sh [runtime]     writes artifacts/dist/yar-<version>-<runtime>.tar.gz and its .sha256
set -euo pipefail

cd "$(dirname "$0")/.."
runtime="${1:-linux-x64}"
version="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' Directory.Build.props)"
out="artifacts/dist/yar-$runtime"
name="yar-$version-$runtime"
id=io.github.mirinio.Yar

rm -rf "$out"
dotnet publish src/Presentation/Yar.Gtk -c Release -r "$runtime" --self-contained -o "$out"
dotnet publish src/Presentation/Yar.Cli -c Release -r "$runtime" --self-contained -o "$out"

# What a person who downloads the tarball needs to install it and to know what it is.
mkdir -p "$out/data"
cp "data/$id.desktop" "data/$id.metainfo.xml" "$out/data/"
cp assets/cube_logo.svg "$out/data/$id.svg"
cp scripts/install-local.sh scripts/uninstall-local.sh README.md LICENSE CHANGELOG.md "$out/"

tar -C artifacts/dist --transform "s|^yar-$runtime|$name|" -czf "artifacts/dist/$name.tar.gz" "yar-$runtime"
(cd artifacts/dist && sha256sum "$name.tar.gz" > "$name.tar.gz.sha256")
echo "Wrote artifacts/dist/$name.tar.gz and $name.tar.gz.sha256"
