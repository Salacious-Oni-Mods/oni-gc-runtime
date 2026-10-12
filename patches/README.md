# Runtime patches

`b3-markbits/` is the patch set the released runtime is built from (`PATCH=b3-markbits
tools/build-runtime.sh`). `mono.patch` applies to the Unity Mono checkout (`ea2b094`), and
`bdwgc.patch` to its `external/bdwgc` (`1113fef`). Each is a `git diff` from the pinned tree; the
build applies them and reverts them afterwards.

| File | Change |
|---|---|
| `mono.patch`, `msvc/libgc.vcxproj` | `PARALLEL_MARK=1` and `USE_MARK_BITS=1` in the x64 Release libgc defines: marking runs on several threads, with one mark bit per granule set by atomic OR (`gc_pmark.h:174-207`) instead of the mark byte `PARALLEL_MARK` otherwise picks (`gcconfig.h:3404`) |
| `mono.patch`, `mono/metadata/boehm-gc.c` | an `oni_gc_version()` export returning `<VERSION>-<set>+<commit>` from a header `build-runtime.sh` generates, so a running game can tell which runtime it is on |
| `bdwgc.patch`, `mark.c` | `GC_mark_some`'s parallel branch calls Unity's mark-stack-empty hook, as the serial branch already does. Unity's `ConditionalWeakTable` (ephemeron) support depends on that hook; without this change, turning on `PARALLEL_MARK` breaks it |
| `bdwgc.patch`, `win32_threads.c` | `GC_MARKERS` is read through `GetEnvironmentVariableA` (the build defines `NO_GETENV`, which otherwise disables it), and marker threads are named `BDWGC marker` |

`VERSION` is the runtime's version, and `expect.txt` lists strings the build must find in the DLL,
so a patch that did not reach the compiler fails the build instead of producing a mislabelled
runtime.
