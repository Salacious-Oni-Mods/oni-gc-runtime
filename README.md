# oni-gc-runtime

Shorter garbage-collection freezes for Oxygen Not Included, in two independent parts:

1. **A rebuilt Mono runtime** (`mono-2.0-bdwgc.dll`) with parallel marking turned on. The game's
   periodic freeze is a full garbage collection, and its cost grows with the colony. On colonies
   with a 2-3.5 GB managed heap, a collection takes about 220-240 ms on the runtime the game ships
   and about 30-50 ms on this one with 16 marker threads. With a single marker thread it costs
   within about 5% of the stock runtime.
2. **GC Timing**, a small mod that moves a collection to a moment you do not see it: when the game
   window loses focus, if enough has been allocated since the last collection to be worth it. It
   also shows which runtime is loaded, on the main menu's build watermark (`GC STOCK` or
   `GC 0.1.0-b3-markbits`) and in `Player.log`.

Either part works without the other, and neither needs any other mod. Neither changes gameplay or
save files.

**Status: alpha.**

## Before you install the runtime: what you are trusting

- **Which file is replaced.** One: `<game>\MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll`, the
  Mono runtime all of the game's code (and every mod's) runs on. Nothing else in the game folder
  and nothing in your saves is touched.
- **Why it has to be replaced.** The garbage collector is native code inside that DLL. A mod runs
  as managed code on top of the runtime and can choose *when* a collection happens (that is what
  GC Timing does), but not *how* the collector marks memory. Marking on several threads needs a
  rebuilt runtime.
- **What it is built from.** Unity's public Mono source, at the exact commit the game's own runtime
  was built from, plus the two patch files in `patches/b3-markbits/` in this repo. Everything
  needed to rebuild it is here ([Building it yourself](#building-it-yourself)), so you do not have
  to take the binary on trust.
- **What the installer does.** It refuses unless the game's current runtime matches a hash in
  `supported-runtimes.txt`, keeps that runtime beside it as `mono-2.0-bdwgc.dll.vanilla`, checks the
  backup byte for byte, then copies the replacement in and checks it. `install.ps1 -Status` only
  reports. Details: [Installing](#installing).
- **How to undo it.** `uninstall.cmd` puts the backed-up original back. Steam's **Verify integrity
  of game files** also restores it, without needing the backup. Details: [Removing](#removing).
- **What a game update does.** An update that ships a runtime puts the game's own file back, so you
  are on the stock runtime again. Install again only with a release that lists the new runtime's
  hash; the installer refuses one it does not list.

## The runtime

### What it changes

Installing replaces one file:

    <game>\MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll

It is built from Unity's public Mono source at the exact commit the game's own runtime was built
from, with the garbage collector modified. The changes are listed in `NOTICE` and are the two
files in `patches/b3-markbits/`:

- the collector marks on several threads (`PARALLEL_MARK`), one bit per object (`USE_MARK_BITS`);
- the parallel marking path calls the hook Unity's `ConditionalWeakTable` support depends on, which
  the unmodified source calls only on the serial path;
- the `GC_MARKERS` environment variable sets the number of marker threads (default: from the
  processor count), and the threads are named `BDWGC marker`;
- an `oni_gc_version()` export names the build.

Nothing else in the runtime is changed.

### Installing

Releases carry a package with the runtime, an installer and the licence notices.

1. Check the download: compare the SHA-256 of `mono-2.0-bdwgc.dll` with the one on the release
   page (`Get-FileHash .\mono-2.0-bdwgc.dll -Algorithm SHA256` in PowerShell).
2. Close the game.
3. Double-click `install.cmd`.

The installer finds the game through Steam (or takes `-GamePath "<folder>"`). It proceeds only if
the game's current runtime is one listed in `supported-runtimes.txt`, keeps that runtime as
`mono-2.0-bdwgc.dll.vanilla` and checks the copy byte for byte, then puts the replacement in place
and checks it. `install.ps1 -Status` reports what is installed and changes nothing.

### Removing

Close the game and double-click `uninstall.cmd`, which puts the game's own runtime back. Steam's
**Verify integrity of game files** does the same, and so does a game update. After an update,
install again only with a release that lists the new runtime; the installer refuses one it does not
list.

### Building it yourself

Because you are replacing the runtime the game's code runs on, you should be able to check what you
run. The build needs Windows with Visual Studio build tools including the MSVC v142 toolset (the one
Unity used), Git for Windows, and WSL for the scripts:

    export ONI_GC_BUILD='D:\oni-gc-build'                 # any Windows folder, outside the repo
    tools/fetch-mono.sh                                   # Unity's mono at the pinned commits
    TOOLSET=v142 PATCH=b3-markbits tools/build-runtime.sh # patch, build, revert the patch
    installer/package.sh <the built mono-2.0-bdwgc.dll>   # the release package

`ONI_GC_BUILD` is the Windows folder the source is fetched into and built in; it can also be set
in `tools/local.env`, which is not committed. The build writes its result to
`<ONI_GC_BUILD>\out\`.
`patches/b3-markbits/expect.txt` lists the strings the build checks for in the result.

## GC Timing (the mod)

Install it like any other mod (`mod/GCTiming`). With the runtime the game ships, it changes
nothing you will notice except the watermark field; its focus-loss collection is where it helps.

- **On focus loss** (alt-tab, minimise) it forces one collection, but only if the unpaused play
  since the last collection has reached half the median of the last 8 intervals between the
  game's own collections (never under 5 s). Finishing a collection resets the allocation counter
  that triggers the next one, so the game's next freeze moves a full interval later.
- **On pause** (off by default, `"collectOnPause": true` turns it on) it collects only if it has
  timed collections and the worst of the last 16 fits a budget (80 ms by default). On the stock
  runtime that is never true, so it never collects at a pause there. When measured, collecting at
  pauses did not reduce the collections during play, which is why it is off by default.
- The game keeps Unity's automatic GC mode. Nothing is deferred and nothing has to be restored.
- It patches one method, `BuildWatermark.RefreshText`, to add the GC field to the watermark.

Settings go in an optional `config.json` next to `GCTiming.dll`; the keys and defaults are in
`mod/GCTiming/README.md`. Every decision the mod takes is one line in `Player.log`.

## Compatibility

- Game build 744825 (Unity 6000.3.5f2). Neither part depends on DLC content.
- The runtime installer checks the game's runtime by hash and refuses any other.
- Other mods that collect garbage themselves keep working; this mod adds at most one collection
  per focus loss.

## Credits

Oxygen Not Included is developed and published by Klei Entertainment. Unity and Mono are developed
by Unity Technologies. This project is not affiliated with or endorsed by either.
How this project follows Klei's modding guidelines, rule by rule: [COMPLIANCE](https://github.com/Salacious-Oni-Dev/oni-sdk-docs/blob/main/COMPLIANCE.md).

The runtime is built from Unity's Mono and its Boehm-Demers-Weiser collector; their notices are in
`NOTICE` and `licenses/`. The idea of a parallel-marking runtime for this game was first published
by the ONI Boost Kit mod, and the idea of moving collections to quiet moments by the GCBudget mod;
no code from either is used.

Development of this project uses AI coding assistants. All changes are reviewed, tested and
released by the maintainer.

## Licence

MIT for the code in this repository (`LICENSE`). The released runtime carries the licences of the
code it is built from; see `NOTICE`.
