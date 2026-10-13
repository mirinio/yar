#!/usr/bin/env bash
# Builds a self-contained tarball of Yar that runs without .NET installed.
# GTK 4.14+ and libadwaita 1.5+ must be present on the computer that runs it.
set -euo pipefail

cd "$(dirname "$0")/.."
runtime="${1:-linux-x64}"
out="artifacts/dist/yar-$runtime"

rm -rf "$out"
dotnet publish src/Presentation/Yar.Gtk -c Release -r "$runtime" --self-contained -o "$out"
dotnet publish src/Presentation/Yar.Cli -c Release -r "$runtime" --self-contained -o "$out"
tar -C artifacts/dist -czf "artifacts/dist/yar-$runtime.tar.gz" "yar-$runtime"
echo "Wrote artifacts/dist/yar-$runtime.tar.gz"
