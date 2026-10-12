#!/usr/bin/env bash
# Fetch Unity's Mono at exactly the commit the game ships, plus the BDWGC submodule it pins.
#
# The checkout lives on the WINDOWS side, not in this repo: the runtime is built with MSVC, and
# MSBuild over \\wsl.localhost paths is slow and unreliable. Windows git (git.exe) does the
# fetch, so line endings and long paths are what the MSVC build expects.
#
#   ONI_GC_BUILD='D:\oni-gc-build' tools/fetch-mono.sh     # checks out to <ONI_GC_BUILD>\mono
#
# ONI_GC_BUILD can also be set in tools/local.env (not committed), which both scripts read.
#
# Shallow on purpose: only the pinned commit is fetched, never the history. Only
# external/bdwgc (and its libatomic_ops) and external/corefx are initialised.
#
# Idempotent: a checkout already at the pinned commits is verified and left alone. A checkout
# at any other commit is REFUSED rather than moved, because it may hold a patched build.
set -euo pipefail

MONO_URL=https://github.com/Unity-Technologies/mono.git
MONO_SHA=ea2b094073e83cb1d047f0235569901096bdcd9e
BDWGC_SHA=1113fefc4ba0e895767ac780d84e9e456fd624db
ATOMIC_SHA=7e44a3b8dcf457207d6394e4e52e327189155e4f  # bdwgc's own submodule, needed by gc_atomic_ops.h
# external/corefx: libmono-dynamic.vcxproj compiles corefx's native brotli sources
# (src/Native/AnyOS/brotli). Without it the build fails with 22 x C1083.
SUBMODULES=(external/bdwgc external/corefx)

HERE="$(cd "$(dirname "$0")/.." && pwd)"
[ -f "$HERE/tools/local.env" ] && . "$HERE/tools/local.env"
: "${ONI_GC_BUILD:?set ONI_GC_BUILD to a Windows folder for the checkout and build, e.g. D:\\oni-gc-build}"
WIN_DIR="$ONI_GC_BUILD\\mono"
DIR="$(wslpath -u "$WIN_DIR")"
GIT=git.exe

g() { (cd "$DIR" && "$GIT" "$@") | tr -d '\r'; }

if [ -d "$DIR/.git" ]; then
  have="$(g rev-parse HEAD)"
  if [ "$have" != "$MONO_SHA" ]; then
    echo "fetch-mono: $WIN_DIR is at $have, not $MONO_SHA. Refusing to move it." >&2
    exit 2
  fi
  echo "fetch-mono: mono already at $MONO_SHA"
else
  mkdir -p "$DIR"
  g init -q
  g config core.longpaths true
  g remote add origin "$MONO_URL"
  echo "fetch-mono: fetching mono $MONO_SHA (shallow)"
  g fetch -q --depth 1 origin "$MONO_SHA"
  g -c advice.detachedHead=false checkout -q FETCH_HEAD
fi

for sm in "${SUBMODULES[@]}"; do
  g submodule update --init --depth 1 -- "$sm"
done
# bdwgc includes atomic_ops.h from ITS submodule. Without it libgc fails with C1083.
(cd "$DIR/external/bdwgc" && "$GIT" submodule update --init --depth 1 -- libatomic_ops) | tr -d '\r'

got_mono="$(g rev-parse HEAD)"
got_gc="$(cd "$DIR/external/bdwgc" && "$GIT" rev-parse HEAD | tr -d '\r')"
[ "$got_mono" = "$MONO_SHA" ] || { echo "fetch-mono: mono is $got_mono, expected $MONO_SHA" >&2; exit 1; }
got_ao="$(cd "$DIR/external/bdwgc/libatomic_ops" && "$GIT" rev-parse HEAD | tr -d '\r')"
[ "$got_gc" = "$BDWGC_SHA" ]  || { echo "fetch-mono: bdwgc is $got_gc, expected $BDWGC_SHA" >&2; exit 1; }
[ "$got_ao" = "$ATOMIC_SHA" ] || { echo "fetch-mono: libatomic_ops is $got_ao, expected $ATOMIC_SHA" >&2; exit 1; }
echo "fetch-mono: OK  mono $got_mono  bdwgc $got_gc  libatomic_ops $got_ao  at $WIN_DIR"
