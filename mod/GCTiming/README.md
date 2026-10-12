# GC Timing

One mod, two jobs, neither of which changes gameplay:

1. **It moves a garbage collection to a moment you cannot see.** When the game window loses focus
   (you alt-tab, or minimise), the mod forces one collection, but only if enough has happened
   since the last one to be worth it. It can also do it **when you pause**, but that is **off by
   default**, because measured in play it removed no collections (see below). The game keeps
   Unity's own automatic GC mode, so nothing is held back, nothing has to be restored, and a game
   in which neither trigger fires behaves exactly like an install without this mod.
2. **It tells you which Mono GC runtime is loaded**, on the build watermark on the main menu
   (` | GC 0.1.0-b3-markbits`, or `GC STOCK` for the game's own runtime; the detail is in the
   tooltip) and in `Player.log`. That is how you check whether the parallel-marking runtime
   install took effect.

## Why forcing a collection early is free

A collection is triggered by how much has been allocated since the last one, and finishing a
collection resets that counter (`alloc.c:1150` in the runtime's source). So a collection forced
while you are in a browser pushes the game's next one a full interval further away. It does not
remove a whole collection: it saves only the part of the interval that had already passed, so the
later it lands the more it saves, and some extra collection work is real. What you get is that
work, and the freeze, happening when you are not looking at the game.

## Collecting when you pause (off by default)

Measured against itself switched off, in four 30-minute games alternating on, off, off, on, with a
pause every three minutes of play: the game ran **50 collections during play with it on and 41
with it off**, the frames holding them were the same (median 75 ms and 74 ms), and the mod's own
pause collections came on top, a third more collection work in total. It works as designed and
buys nothing, so it is opt-in: `"collectOnPause": true` in `config.json`. The rest of this section
is how it behaves when on.

Pausing is the other moment a collection could be moved to: you have stopped the colony to think,
and the game is about to allocate little for a while. But you are *looking at the game*, so the
freeze is not free the way an alt-tab is. A collection on the game's own runtime takes about
**220 ms**, a quarter-second freeze you would see.

So the pause trigger has a condition the focus trigger does not: **the mod must already know that
a collection fits a budget (80 ms by default) before it will spend one on a pause.** It finds out
at the end of a load, by making one collection there and timing it: the game is still on the
loading screen, so that freeze costs you nothing, and it is the same scene change the game already
collects on. Every collection it makes at a pause after that is timed the same way. It judges by
**the most expensive of the last 16 timings**, so one cheap collection cannot talk it into a
freeze, while one expensive one switches the trigger off. In practice:

- **On the game's own runtime**, the first measurement is ~220 ms, the budget refuses it, and the
  pause trigger never fires.
- **With the parallel-marking runtime installed**, a collection is ~45 ms, the budget allows it,
  and pausing moves a collection out of your play.
- **If your colony grows** until collections stop fitting the budget, the trigger switches itself
  off again, and only a load re-measures it. Collections get more expensive as a colony grows,
  never cheaper, so a gate that has closed stays closed until something resets the heap.

The collection lands two frames after the pause, so the pause overlay has drawn first.

**The mod does not guess what a collection cost; it times one.** Every number the pause budget is
checked against is stopwatch time around a collection this mod itself asked for. Inferring the
cost from the frame that held a collection does not work: a frame also holds whatever else the
game did in it (a save, the screenshot taken when a duplicant dies), and a 506 ms frame can hold a
33 ms collection.

```
[GCTiming] cost timed 33.1 ms by stopwatch (load-end probe); the gate now reads 33.1 ms, the worst of 1
```

## When it decides to collect

It measures the seconds of **unpaused play** between the collections the game runs on its own,
and keeps the last 8. Those are real seconds, timed by the mod's own stopwatch (Unity's per-frame
clock can run well below wall-clock time). On either trigger it collects only if the play since
the last collection has reached **half the median of those intervals**, with a floor of 5 seconds.
Until three intervals exist the floor alone decides. That way the mod calibrates itself to your
colony, your game speed and your runtime instead of carrying a number guessed on another machine.
It is also what stops a run of space-bar presses costing a freeze each: a paused game adds nothing
to the clock, so the second pause in a row has no play behind it and is refused.

Every decision is one line in `Player.log`. A decision that repeats (pausing again and again on
the game's own runtime gives the same answer every time) is logged once and then counted:

```
[GCTiming] collect on focus loss: 27.4 s of play, threshold 11.5 s (median 23.0 s of 8), busy by stopwatch 214 ms
[GCTiming] skip on focus loss: 3.1 s of play, threshold 11.5 s (median 23.0 s of 8)
[GCTiming] collect on pause: 19.8 s of play, threshold 11.5 s (median 23.0 s of 8), busy by stopwatch 46 ms
[GCTiming] cost timed 46.0 ms by stopwatch; the gate now reads 46.0 ms, the worst of 4
[GCTiming] skip on pause: the worst of the last 16 collections took 242.0 ms (timed), over the 80.0 ms pause budget (18.3 s of play, threshold 11.5 s (median 23.0 s of 8))
```

## config.json (optional)

There is no settings screen, and the mod never writes a config file: a Workshop update replaces
the mod's folder, and a file written there would be destroyed with it. To change anything, create
`config.json` next to `GCTiming.dll`:

```json
{
  "collectOnFocusLoss": true,
  "collectOnPause": false,
  "pauseBudgetMs": 80.0,
  "floorSeconds": 5.0,
  "fraction": 0.5,
  "window": 8
}
```

| key | default | what it does |
|---|---|---|
| `collectOnFocusLoss` | `true` | `false` switches the focus trigger off |
| `collectOnPause` | `false` | `true` switches the pause trigger on (with both triggers off, the mod only reports the runtime) |
| `pauseBudgetMs` | `80.0` | the most a collection may cost for one to be spent on a pause (1-5000). Raise it above ~250 if you would rather have the freeze at your pause than in your play on the game's own runtime; it is not the default because the freeze is visible |
| `floorSeconds` | `5.0` | never collect if less than this much play has happened (0.5-600) |
| `fraction` | `0.5` | the share of the median interval that counts as "worth it" (0.05-4.0) |
| `window` | `8` | how many recent intervals the median is taken over (2-64) |

Every key is optional, a value out of range is clamped, and a file that cannot be read costs one
warning line in `Player.log` and the defaults. A broken config never stops the mod or the game.

## What it patches

One Harmony postfix, on `BuildWatermark.RefreshText`, to append the GC field to a cosmetic label.
Nothing else. The timing policy patches nothing: it reads the pause state and Unity's own focus
message from its own component.

If another mod already writes a GC field on that line, this one stands down and says so in the
log, so the watermark never carries two.
