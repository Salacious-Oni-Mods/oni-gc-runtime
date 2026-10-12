#!/usr/bin/env bash
# Assemble the end-user package: installer/dist/oni-gc-runtime-<version>.zip
#
#   installer/package.sh <built mono-2.0-bdwgc.dll>
#
# Takes the runtime built by tools/build-runtime.sh (PATCH=b3-markbits) and puts it beside
# install.ps1, the two .cmd launchers, supported-runtimes.txt and README.txt, NOTICE and licenses/, with SHA256SUMS and
# VERSION written here. The .cmd and .txt files get CRLF line endings, for Notepad and cmd.exe.
#
# When ONI_GAME is set, this refuses to package unless that game's own runtime is listed in
# supported-runtimes.txt, so a package cannot list a runtime it was not built to replace.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(dirname "$here")
die() { echo "package: $*" >&2; exit 1; }

dll=${1:-}
[ -n "$dll" ] && [ -f "$dll" ] || die "usage: installer/package.sh <built mono-2.0-bdwgc.dll>"

version=$(sed -n '/^[^#[:space:]]/{p;q}' "$root/patches/b3-markbits/VERSION")
[ -n "$version" ] || die "patches/b3-markbits/VERSION has no version line"
grep -aq "oni_gc_version" "$dll" || die "$dll has no oni_gc_version export; it is not a runtime built from patches/"

if [ -n "${ONI_GAME:-}" ]; then
  rt=$ONI_GAME/MonoBleedingEdge/EmbedRuntime
  found=
  # The game's own runtime may sit under the name the installer or a manual install left it.
  for f in mono-2.0-bdwgc.dll mono-2.0-bdwgc.dll.vanilla; do
    [ -f "$rt/$f" ] || continue
    h=$(sha256sum < "$rt/$f" | cut -d' ' -f1)
    if grep -q "^$h " "$here/supported-runtimes.txt"; then found=$f; break; fi
  done
  [ -n "$found" ] || die "no mono-2.0-bdwgc.dll under $rt is a runtime listed in supported-runtimes.txt"
  echo "replaces: $rt/$found ($(grep "^$h " "$here/supported-runtimes.txt" | cut -d' ' -f2-))"
else
  echo "package: ONI_GAME is not set, so the supported runtime list is not checked" >&2
fi

name=oni-gc-runtime-$version
out=$here/dist/$name
rm -rf "$out" "$out.zip"
mkdir -p "$out"

cp "$dll" "$out/mono-2.0-bdwgc.dll"
cp "$here/install.ps1" "$out/"
for f in install.cmd uninstall.cmd README.txt supported-runtimes.txt; do
  sed 's/\r$//; s/$/\r/' "$here/$f" > "$out/$f"
done
# The licence notices the runtime's source asks a redistributed binary to carry.
sed 's/\r$//; s/$/\r/' "$root/NOTICE" > "$out/NOTICE.txt"
mkdir -p "$out/licenses"
for f in "$root"/licenses/*; do
  sed 's/\r$//; s/$/\r/' "$f" > "$out/licenses/$(basename "$f")"
done
printf '%s\r\n' "$version" > "$out/VERSION"
(cd "$out" && sha256sum mono-2.0-bdwgc.dll > SHA256SUMS)

(cd "$here/dist" && zip -qr "$name.zip" "$name")
echo "packaged $out.zip"
cat "$out/SHA256SUMS"
