using System;
using System.Collections.Generic;

// The timing decision: is a collection worth forcing right now?
//
// WHY THIS IS ITS OWN CLASS, AND WHY IT KNOWS NOTHING ABOUT UNITY. Everything the policy decides
// is arithmetic over a list of seconds and a flag, and neither needs a game to be tested. The
// pause EDGE is in here for that reason: counting frames after a transition is two comparisons
// with an off-by-one in each. FocusCollector reads Unity's flag and does what this class says.
//
// WHAT IT DECIDES. The mod collects on focus loss and, when enabled, on pause. A collection is only
// worth forcing if enough has been allocated since the last one that the next automatic one is
// close. There is no allocation counter on this runtime (`GC.GetAllocatedBytesForCurrentThread`
// returns 0, and the game's runtime does not export `GC_get_total_bytes`), so the mod measures the
// one thing it can see: the seconds of UNPAUSED PLAY between the collections that happen on their
// own. The threshold is half the median of the last 8 of those, with a floor, so it calibrates
// itself to the colony, the game speed and the runtime instead of carrying a guessed number.
//
// WHY A MEDIAN OF INTERVALS AND NOT A BUDGET IN MB. A fixed megabyte budget fits one machine and
// one colony. The median of what the game just did needs no such number, and it moves on its own
// when the colony grows.
//
// WHY "ANY COLLECTION RESETS THE CLOCK". `GC_finish_collection` zeroes `GC_bytes_allocd`
// (`alloc.c:1150`), and `GC.Collect()` reaches it through `mono_gc_collect` -> `GC_gcollect`
// (`boehm-gc.c:348`). So after ANY collection the automatic trigger starts over, and the seconds
// since the last one are the right measure of how close the next one is, whoever caused it.
//
// WHY PAUSE NEEDS A SECOND GATE. A paused game should hold a 3.3 ms frame (300 fps), and a
// collection on a multi-GB heap costs far more than that: about 220-240 ms on the game's own
// runtime, about 42-48 ms median on the parallel-marking runtime. No stop-the-world collection
// meets 3.3 ms, so the pause trigger spends ONE hitch at the transition and must know that hitch
// is small before it spends it. `CostAllowsPause` is that requirement: a MEASURED collection at or
// under a budget in milliseconds, never an assumption about which runtime is loaded. On the game's
// own runtime the first measurement is ~242 ms, the budget refuses it, and pause stays off; on the
// parallel-marking runtime it is ~46 ms and pause arms. A colony big enough to push the patched
// runtime over the budget disarms it again on its own.
//
// WHERE THE MEASUREMENT COMES FROM: A STOPWATCH AROUND OUR OWN COLLECTION, AND NOTHING ELSE.
// Every number this gate decides on is `Stopwatch` time around a `GC.Collect()` the mod itself
// called. No cost is inferred from how long a frame took, because that estimate is wrong in both
// directions. Measured against the profiler's own reading of the same collections:
//
//   estimate 83.9 ms   frame 107.2 ms   the collection was 47.92 ms
//   estimate 95.9 ms   frame 126.0 ms   the collection was 51.94 ms
//   estimate 482.9 ms  frame 505.9 ms   the collection was 32.59 ms
//
// The first two are roughly 2x high: the subtracted "normal frame" median describes frames that did
// NOT collect, and a collection lands on an allocation-heavy frame by construction. The third
// frame also held the game's retired-colony screenshot. The set of things a frame can hold besides
// a collection is open, so no rule that names them one at a time can fix the estimate. The reverse
// also happens: a 217 ms collection has been read off its frames as 16.7 ms, because
// `GC.CollectionCount` moved two frames after the frame that stalled.
//
// WHERE THE FIRST MEASUREMENT COMES FROM: THE END OF A LOAD. A player loses focus rarely, so a gate
// fed only by focus-loss collections would almost never arm. The end of a load is free: the game
// is already frozen behind the loading screen, the game collects on that scene change anyway, and
// the heap is the colony's rather than the main menu's. The mod needs no patch to find it --
// `Game.Instance.GetInstanceID()` changes, the same edge that drives `Resync`. One collection
// there, timed, gives the gate an honest reading before the player has done anything. After that
// the pause trigger feeds itself: every pause collection is timed the same way.
//
// WHY ONE READING ARMS THE GATE. A stopwatch around a collection we called is a direct reading of
// a full collection, at a known moment, of known cause. (`GC.CollectionCount` moves about twice as
// often as the profiler reports a collection, which is another reason counted collections are
// never used for cost.) The WINDOW stays, judged by its worst, and keeps its asymmetry: one high
// reading closes the gate, because reading low costs the player a freeze and reading high costs
// only a collection we could have had.
//
// WHAT THIS GIVES UP. A gate shut by an over-budget reading takes no further readings from pauses,
// because it is refusing them, so it reopens only at the next load or focus loss. That is nearly
// always correct: collection cost climbs with the heap (measured: median 11.7 ms in the first 15
// minutes of a 105-minute game, 73.2 ms in the last, max 123.6 ms, heap 58 MB to 6031 MB), and a
// cost that only grows is a gate that should only close. A load re-probes it, which is the one
// event that can legitimately make a collection cheaper.
//
// ONE APPROXIMATION. A collection we did not ask for is recorded as an interval, and some of those
// are the game's own: it collects after every save and on every scene change. Those intervals are
// shorter than the allocation-driven ones, so they pull the median down and make the mod slightly
// more eager. That is the safe direction -- an extra collection while the window is not focused
// costs the player nothing -- and managed code cannot tell the two apart.
namespace GCTiming
{
	internal sealed class CollectPolicy
	{
		internal const double DefaultFloorSeconds = 5.0;
		internal const double DefaultFraction = 0.5;
		internal const int DefaultWindow = 8;

		// The most a collection may have cost, measured, before the mod is willing to spend one at
		// a pause transition. 80 ms clears the parallel-marking runtime (41.7-48.3 ms median,
		// 70.7 ms max on a 3.2-3.5 GB heap) with headroom, and refuses the game's own runtime's
		// ~242 ms. It is about 24 frames of the 3.3 ms paused budget, spent once per pause at most.
		internal const double DefaultPauseBudgetMs = 80.0;

		// Frames between seeing a pause and deciding on it, so the pause overlay draws before the
		// freeze: a freeze behind a screen that has not changed yet reads as a dropped input.
		internal const int PauseDelayFrames = 2;

		// Below this many observed intervals the median means nothing, so the floor decides.
		internal const int MinimumIntervals = 3;

		// HOW MANY MEASUREMENTS THE PAUSE GATE DECIDES ON. The gate is decided by the WORST of the
		// last CostWindow measurements, and refuses until MinimumCostSamples of them exist. One
		// high reading closes it; the asymmetry is deliberate, because reading high costs a pause
		// collection we could have had and reading low costs the player a freeze.
		internal const int CostWindow = 16;
		// ONE direct reading is enough to arm: a stopwatch around a collection we called cannot be
		// a partial collection and cannot have missed the stall (the header says why frame-derived
		// readings can). The window and its worst-of rule keep the other half of the asymmetry.
		internal const int MinimumCostSamples = 1;

		private readonly double floorSeconds;
		private readonly double fraction;
		private readonly int window;
		private readonly double pauseBudgetMs;
		private readonly List<double> intervals = new List<double>();

		private double playSeconds;
		private int lastCount = -1;
		private bool wasPaused;
		// Frames since the pause was seen, or -1 when this pause is decided or none is on.
		private int pauseFrames = -1;
		// Negative means "nothing measured yet", which is not the same as "cheap".
		private double lastBusyMs = -1.0;
		// The last CostWindow measurements, newest last. Every one is stopwatch time around a
		// collection this mod called; there is no other kind any more.
		private readonly List<double> costs = new List<double>();
		// The inputs the last cost measurement was made from. A gate that decides on a number has
		// to be able to show where the number came from: a pause refused or allowed on a cost is
		// otherwise unauditable from a log. Taken by the Unity half once per change and logged.
		private string costNote;
		// Set at Resync, cleared when the load-end probe has been taken. The Unity half asks for it
		// with `ProbeDue`, because forcing a collection is not something a policy class can do.
		private bool probeDue;
		// The pause budget's last answer in this world: -1 none yet, 0 refused, 1 allowed. Only a
		// CHANGE of answer is news, and the Unity half logs a change even past its line cap: in a
		// long game the cap can be reached before the one refusal that matters, and the log must
		// still show it.
		private int budgetAnswer = -1;

		internal CollectPolicy(double floorSeconds, double fraction, int window)
			: this(floorSeconds, fraction, window, DefaultPauseBudgetMs)
		{
		}

		internal CollectPolicy(double floorSeconds, double fraction, int window,
			double pauseBudgetMs)
		{
			this.floorSeconds = floorSeconds;
			this.fraction = fraction;
			this.window = window < 2 ? 2 : window;
			this.pauseBudgetMs = pauseBudgetMs;
		}

		/// Seconds of unpaused play since the last collection of any kind.
		internal double PlaySeconds { get { return playSeconds; } }

		/// How many automatic intervals have been seen (capped at the window).
		internal int IntervalCount { get { return intervals.Count; } }

		/// The budget a measured collection must fit for the pause trigger to fire, in ms.
		internal double PauseBudgetMs { get { return pauseBudgetMs; } }

		/// What the last collection this mod forced actually cost, or -1 if it has not forced one.
		internal double LastBusyMs { get { return lastBusyMs; } }

		/// Whether any collection has been measured yet. Without one the pause trigger cannot fire.
		internal bool HasCostMeasurement { get { return costs.Count > 0; } }

		/// How many measurements the gate has. Below MinimumCostSamples it refuses outright.
		internal int CostSampleCount { get { return costs.Count; } }

		/// The measurement the gate decides on: the WORST of the window, or -1 with none.
		internal double CostForGateMs
		{
			get
			{
				double worst = -1.0;
				for (int i = 0; i < costs.Count; i++)
					if (costs[i] > worst)
						worst = costs[i];
				return worst;
			}
		}

		/// Whether a load-end cost probe is owed: one collection, timed, where the freeze is
		/// invisible because the game is still on the loading screen. The Unity half takes it and
		/// reports it through `NoteOurCollection`, which clears this.
		internal bool ProbeDue { get { return probeDue; } }

		/// The median of the intervals seen so far, or 0 before there are any.
		internal double MedianInterval
		{
			get
			{
				if (intervals.Count == 0)
					return 0;
				var sorted = new List<double>(intervals);
				sorted.Sort();
				int n = sorted.Count;
				return n % 2 == 1 ? sorted[n / 2] : 0.5 * (sorted[n / 2 - 1] + sorted[n / 2]);
			}
		}

		/// The seconds of play a trigger needs before a forced collection is worth its freeze.
		internal double Threshold
		{
			get
			{
				if (intervals.Count < MinimumIntervals)
					return floorSeconds;
				double t = fraction * MedianInterval;
				return t < floorSeconds ? floorSeconds : t;
			}
		}

		/// One frame. `unpausedSeconds` is 0 while the game is paused, so a paused game never
		/// moves the clock; `collectionCount` is GC.CollectionCount(0), which counts every
		/// collection on this runtime (BDWGC has one generation).
		///
		/// REAL seconds, from the caller's own `Stopwatch`. Unity's `Time.unscaledDeltaTime` is
		/// not a clock in seconds (it has read 73.7% of real time over a 96-minute game), and
		/// `floorSeconds` is a promise in seconds.
		///
		/// NO FRAME LENGTH IS TAKEN, on purpose: cost comes only from `NoteOurCollection` (the
		/// header says why), so the one job here is the interval arithmetic: how much play
		/// happened between collections.
		internal void Observe(double unpausedSeconds, int collectionCount)
		{
			if (unpausedSeconds > 0)
				playSeconds += unpausedSeconds;
			if (lastCount < 0)
			{
				lastCount = collectionCount;
				return;
			}
			if (collectionCount == lastCount)
				return;
			// The count moved: a collection happened, whoever caused it. It resets the clock either
			// way, because any collection zeroes BDWGC's allocation counter.
			Record(playSeconds);
			lastCount = collectionCount;
			playSeconds = 0;
		}

		/// After a collection this mod asked for. It resets the clock like any other collection
		/// but is NOT recorded as an interval: it is the mod's own doing, and feeding it back
		/// into the median would let the policy talk itself into collecting ever sooner.
		internal void NoteOurCollection(int collectionCount)
		{
			lastCount = collectionCount;
			playSeconds = 0;
		}

		/// The same, with what the collection cost by the mod's own `Stopwatch`. The cost is kept
		/// whichever trigger forced it: a focus-loss collection is measured for free, and that
		/// measurement is what the pause trigger is allowed to rely on. The LAST cost is kept
		/// rather than the smallest or an average, because the heap only grows within a game
		/// and the most recent collection is the best estimate of what the next one will cost.
		internal void NoteOurCollection(int collectionCount, double busyMs)
		{
			if (busyMs >= 0)
			{
				bool wasProbe = probeDue;
				AddCost(busyMs);
				probeDue = false;
				costNote = "cost timed " + Fmt(busyMs) + " ms by stopwatch"
					+ (wasProbe ? " (load-end probe)" : string.Empty)
					+ "; the gate now reads " + Fmt(CostForGateMs) + " ms, the worst of "
					+ costs.Count;
			}
			NoteOurCollection(collectionCount);
		}

		/// Whether a measured collection is cheap enough to be spent at a pause transition, and
		/// the sentence a decision line prints either way. `false` when nothing has been measured:
		/// an unmeasured collection is never spent on a screen the player is looking at.
		/// The last cost measurement's arithmetic, once, or false if nothing new was measured.
		// One measurement, however it was obtained. The newest is kept for reporting; the window
		// is what the gate reads.
		private void AddCost(double ms)
		{
			lastBusyMs = ms;
			costs.Add(ms);
			while (costs.Count > CostWindow)
				costs.RemoveAt(0);
		}

		internal bool TakeCostNote(out string note)
		{
			note = costNote;
			costNote = null;
			return note != null;
		}

		/// Record the pause budget's answer at a decision, and say whether it differs from the last
		/// one given in this world. The first answer after a load always counts as a change, so each
		/// world's log opens with where its gate stands. Bounded by the answers flipping, which needs a
		/// new timed collection each time, so a caller may log it without a cap.
		internal bool BudgetAnswerChanged(bool allowed)
		{
			int answer = allowed ? 1 : 0;
			bool changed = answer != budgetAnswer;
			budgetAnswer = answer;
			return changed;
		}

		internal bool CostAllowsPause(out string why)
		{
			if (costs.Count < MinimumCostSamples)
			{
				why = "no collection timed yet, so the " + Fmt(pauseBudgetMs)
					+ " ms pause budget cannot be checked"
					+ (probeDue ? " (the load-end probe is still owed)" : string.Empty);
				return false;
			}
			double worst = CostForGateMs;
			if (worst > pauseBudgetMs)
			{
				why = "the worst of the last " + costs.Count + " collections took " + Fmt(worst)
					+ " ms (timed), over the " + Fmt(pauseBudgetMs) + " ms pause budget";
				return false;
			}
			why = "the last " + costs.Count + " collections cost at most " + Fmt(worst)
				+ " ms (timed), within the " + Fmt(pauseBudgetMs) + " ms pause budget";
			return true;
		}

		/// Entering a world, or a new one after a load. Drops the clock without recording an
		/// interval: the seconds either side of a load are not one allocation-driven stretch, and
		/// the collections Klei runs during the load are not ours to count.
		internal void Resync(int collectionCount)
		{
			Resync(collectionCount, false);
		}

		/// The same, ADOPTING the world's pause state instead of comparing against it. Every load
		/// comes up paused, and that is not the player pausing: adopted, it raises no edge.
		internal void Resync(int collectionCount, bool paused)
		{
			lastCount = collectionCount;
			playSeconds = 0;
			wasPaused = paused;
			pauseFrames = -1;
			// THE COST WINDOW GOES. The load this mod always sees is the FIRST one, from a main menu
			// whose heap is about 75 MB into a colony of 2 GB, where the same collection costs 20 ms
			// and 220 ms; a main-menu measurement kept across it would let the first pause in a
			// colony spend a 220 ms freeze.
			// And a cleared window is why a load OWES a probe: the only thing that can refill it is
			// a collection this mod times, so a load that cleared it and probed nothing would leave
			// the pause trigger unarmed for the rest of the game.
			costs.Clear();
			lastBusyMs = -1.0;
			probeDue = true;
			budgetAnswer = -1;
		}

		/// The pause edge, fed the paused flag once a frame. Returns true on the ONE frame the
		/// pause trigger should decide: PauseDelayFrames frames after the transition into paused,
		/// once per pause. Holding the game paused decides once, not every frame; a pause that
		/// ends before the delay is up decides not at all; and repeated space-bar presses each
		/// raise an edge, which the interval threshold is what refuses (a paused game adds no play
		/// to the clock, so the second press has nothing behind it).
		internal bool PauseTick(bool paused)
		{
			bool decide = false;
			if (!paused)
				pauseFrames = -1;
			else if (!wasPaused)
				pauseFrames = 0;
			else if (pauseFrames >= 0 && ++pauseFrames >= PauseDelayFrames)
			{
				pauseFrames = -1;
				decide = true;
			}
			wasPaused = paused;
			return decide;
		}

		internal bool ShouldCollect(out double threshold)
		{
			threshold = Threshold;
			return playSeconds >= threshold;
		}

		/// What the policy is, for the log line at load and the watermark tooltip.
		internal string Describe()
		{
			return "collect when play since the last collection reaches " + Fmt(fraction)
				+ " x the median of the last " + window + " intervals (floor " + Fmt(floorSeconds)
				+ " s); a pause also needs a measured collection within " + Fmt(pauseBudgetMs)
				+ " ms";
		}

		/// What the policy currently knows, for a decision line.
		internal string State()
		{
			return Fmt(playSeconds) + " s of play, threshold " + Fmt(Threshold) + " s"
				+ (intervals.Count < MinimumIntervals
					? " (floor: only " + intervals.Count + " interval(s) seen)"
					: " (median " + Fmt(MedianInterval) + " s of " + intervals.Count + ")");
		}

		private void Record(double seconds)
		{
			// A collection with no play behind it says nothing about the allocation rate: it
			// happened during a load, a save, or a stretch the mod was not watching.
			if (seconds <= 0)
				return;
			intervals.Add(seconds);
			while (intervals.Count > window)
				intervals.RemoveAt(0);
		}

		private static string Fmt(double d)
		{
			return d.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
		}
	}
}
