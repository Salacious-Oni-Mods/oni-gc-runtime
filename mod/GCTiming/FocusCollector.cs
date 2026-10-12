using System;
using System.Diagnostics;
using UnityEngine;

// The triggers: collect while the player is looking at something else, and (when enabled) at a
// pause.
//
// THE WHOLE POLICY, IN ONE PLACE. The game stays on Unity's automatic mode -- `GCMode` is never
// set, so there is nothing to restore, no heap to grow and no ceiling to get wrong; a game in
// which this component never fires is byte-for-byte vanilla behaviour. When the window loses
// focus, and only then, the mod forces one collection if enough has been allocated since the last
// one to be worth the freeze (CollectPolicy decides that). Because every collection zeroes
// BDWGC's allocation counter, an early collection is not extra work: it takes the next automatic
// collection's place and pushes it a full interval further away. What the player gets is that
// collection happening while they are in a browser instead of mid-frame.
//
// TWO TRIGGERS, AND WHY THEY ARE NOT EQUAL. Focus loss is unconditional beyond the interval
// threshold: the player is not looking, so the freeze costs nothing whatever it is. A PAUSE is
// different -- the player is looking straight at the game, which should hold a 3.3 ms frame
// (300 fps) while paused. That is about the SUSTAINED paused frame rate, and what this trigger
// spends is ONE hitch at the transition, so the trigger is allowed only when the mod has MEASURED
// a collection small enough to spend (`CollectPolicy.CostAllowsPause`, default budget 80 ms; the
// reasoning and where the measurement comes from are in that file's header). On the game's own
// runtime the measurement is ~242 ms and the pause trigger never fires; on the parallel-marking
// runtime early in a game it is ~33-46 ms and it does. The pause trigger is off by default
// (Config.cs says why).
//
// THE MEASUREMENT IS A STOPWATCH, NEVER A FRAME. This component takes exactly one collection the
// player cannot see for free: the first frame of a newly loaded world, where the loading screen is
// still up. That reading arms or refuses the pause trigger, and every pause collection after it
// re-measures by the same stopwatch. Cost inferred from the length of the frame holding a
// collection is roughly 2x the truth on ordinary frames and 15x on one that held a screenshot
// write.
//
// THE PLAY CLOCK IS A STOPWATCH TOO. The other number in this mod's arithmetic is seconds of
// unpaused play, which decides both the self-calibrating threshold and the `floorSeconds` promise
// beneath it. Unity's `Time.unscaledDeltaTime` is not real time: over a 96-minute game it summed
// to 73.7% of the wall clock, pinned at 1/60 s a frame, while a Stopwatch in the same process read
// 1118 ms frames that delta never saw. A ratio against intervals off the same clock survives that;
// "never more often than every 5 s of play" does not (it became 6.8 s). So play is timed by the
// same instrument as the cost.
//
// TWO FRAMES OF DELAY ON A PAUSE, ON PURPOSE. The collection is forced on the second frame after
// the one in which the pause was seen, so the pause overlay and the speed control have drawn
// before the game stops for it. A freeze behind a screen that has not visibly changed yet reads as
// a dropped input; the same freeze behind a drawn pause overlay reads as the pause landing. The
// edge itself is `CollectPolicy.PauseTick`, not code in this file, so the off-by-one-prone part
// lives in the Unity-free half. This component reads the flag and does what the policy says.
//
// THE ASSUMPTION THIS RESTS ON. "Out of focus" means "not being watched". A player with the game
// on a second monitor, watching it while typing elsewhere, would see the freeze. The game makes
// the same assumption -- `GenericGameSettings.sleepWhenOutOfFocus` is true by default, so an
// unfocused game is already throttled -- but the assumption is the mod's to own, which is why
// `collectOnFocusLoss` can be switched off in config.json. The pause trigger rests on no such
// assumption: it is measured, and `collectOnPause` switches it.
//
// NO HARMONY PATCH. Focus arrives as Unity's own `OnApplicationFocus` message, the pause state is
// read by polling `SpeedControlScreen.Instance.IsPaused`, and a finished load shows up as a change
// in `Game.Instance.GetInstanceID()`. The timing policy patches no game method.
//
// NO REFERENCE TO `Game` IS KEPT. The current world is identified by its instance ID, never by a
// field holding the object: a held `Game` keeps the whole previous world alive across a load, and
// that shows up as heap growth caused by the mod that is supposed to be reducing it.
namespace GCTiming
{
	internal sealed class FocusCollector : MonoBehaviour
	{
		// A trigger storm must not fill Player.log. The decisions still happen; only the lines
		// stop. A player alt-tabs a few times an hour but may pause constantly, and on the game's
		// own runtime every one of those pauses produces the SAME skip line (the cost gate's answer
		// does not change within a game). So identical consecutive messages are collapsed as well
		// as capped -- collapsed rather than dropped, because "this decision was taken 300 times"
		// is the useful fact and a bug report that silently omits it is worse than a noisy one.
		// 400 lines cover a long game; a cap of 100 was reached 28 minutes into a 105-minute one.
		private const int MaxLoggedDecisions = 400;

		private static FocusCollector instance;

		private Config config;
		private CollectPolicy policy;
		private int gameId;
		private bool armed;
		private int logged;
		private int collections;
		private double lastBusyMs;
		// Real seconds between Updates, for the play clock: see the header section on it. Restarted
		// every frame, so what it reports is one frame and never a load or a menu.
		private readonly Stopwatch playClock = new Stopwatch();
		private string lastMessage;
		private int repeats;

		/// Called once from OnLoad. Returns what it armed, for the load line.
		internal static string Install(Config config)
		{
			if (instance != null)
				return "already armed";
			try
			{
				var go = new GameObject("GCTiming");
				UnityEngine.Object.DontDestroyOnLoad(go);
				var c = go.AddComponent<FocusCollector>();
				c.config = config;
				c.policy = new CollectPolicy(config.FloorSeconds, config.Fraction, config.Window,
					config.PauseBudgetMs);
				instance = c;
				c.Publish();
				return config.CollectOnFocusLoss || config.CollectOnPause
					? c.policy.Describe()
					: "no trigger armed (both triggers are false in config.json)";
			}
			catch (Exception e)
			{
				Log.Warn("could not arm the timing policy, so the game keeps Unity's own: " + e);
				return "not armed (arming threw)";
			}
		}

		// The world is up and running, and not mid-load.
		private static bool InGame()
		{
			return !App.isLoading && Game.Instance != null && SpeedControlScreen.Instance != null;
		}

		private void Update()
		{
			// Read first and unconditionally, so that a frame the returns below discard -- a load,
			// the main menu, a world that has just come up -- cannot arrive later as play the
			// player never had.
			double frameSeconds = playClock.IsRunning ? playClock.Elapsed.TotalSeconds : 0.0;
			playClock.Restart();
			if (!InGame())
			{
				armed = false;
				return;
			}
			bool paused = SpeedControlScreen.Instance.IsPaused;
			int id = Game.Instance.GetInstanceID();
			if (!armed || id != gameId)
			{
				// A new world: the clock restarts, and the collections Klei ran during the load
				// are not counted as an interval of play. The pause state is ADOPTED rather than
				// compared -- a world that comes up paused is not the player pausing, and must not
				// be read as a transition.
				gameId = id;
				armed = true;
				policy.Resync(GC.CollectionCount(0), paused);
				// THE ONE COLLECTION THE PLAYER CANNOT SEE. The world is up but the loading screen
				// has not gone yet and nothing has been clicked, so a freeze here is part of the
				// load -- and Klei collects on this scene change anyway. It is the only reading the
				// pause gate will have until the first pause it allows, and without it the cleared
				// window would leave that gate shut for the game. Taken only when the pause
				// trigger could actually use it: a player who has switched it off is owed no freeze.
				if (config != null && config.CollectOnPause && policy.ProbeDue && !Held())
					Force("load end");
				return;
			}
			// The play clock, in real seconds and zero while paused, and the collection count the
			// policy watches for the game's own collections. Nothing about a frame goes in.
			policy.Observe(paused ? 0.0 : frameSeconds, GC.CollectionCount(0));
			// The edge is fed every frame whatever the config says, so the switch decides only
			// whether to act, never what the policy believes the player did.
			// Every change to the measured cost, once. These count against MaxLoggedDecisions like
			// any other line, which is why they say the whole arithmetic rather than needing two.
			string costNote;
			if (policy.TakeCostNote(out costNote))
				Report(costNote);
			bool pauseDue = policy.PauseTick(paused);
			if (pauseDue && config != null && config.CollectOnPause)
				Decide("pause", true);
		}

		private void OnApplicationFocus(bool focused)
		{
			if (focused || config == null || !config.CollectOnFocusLoss)
				return;
			// Outside a running world there is nothing to time: the main menu and the loading
			// screens collect on every scene change already (SOURCES.md §1a).
			if (!InGame() || !armed)
				return;
			Decide("focus loss", false);
		}

		// `costGated` is the whole difference between the two triggers: a freeze the player cannot
		// see needs no budget, a freeze on a paused screen does.
		private void Decide(string trigger, bool costGated)
		{
			try
			{
				if (Held())
				{
					Report("skip on " + trigger + ": another mod holds the collector off (GCMode "
						+ UnityEngine.Scripting.GarbageCollector.GCMode + ")");
					return;
				}
				double threshold;
				bool worth = policy.ShouldCollect(out threshold);
				string state = policy.State();
				if (!worth)
				{
					Report("skip on " + trigger + ": " + state);
					return;
				}
				bool budgetFlipped = false;
				if (costGated)
				{
					string why;
					bool allowed = policy.CostAllowsPause(out why);
					budgetFlipped = policy.BudgetAnswerChanged(allowed);
					if (!allowed)
					{
						Report("skip on " + trigger + ": " + why + " (" + state + ")", budgetFlipped);
						return;
					}
				}
				Report("collect on " + trigger + ": " + state + ", busy by stopwatch "
					+ Collect().ToString("0") + " ms", budgetFlipped);
			}
			catch (Exception e)
			{
				Log.Warn("the timing policy threw and is left alone this time: " + e);
			}
		}

		// A collection with nothing to decide: the load-end cost probe. There is no interval to
		// clear (the load just reset the clock) and no budget to check (this reading is what the
		// budget will be checked against), so the two gates in `Decide` would both be wrong here.
		// The line it writes is the cost note, from the policy, on the next frame.
		private void Force(string trigger)
		{
			try
			{
				Collect();
			}
			catch (Exception e)
			{
				Log.Warn("the " + trigger + " cost probe threw, so the pause trigger stays unarmed "
					+ "until the next load: " + e);
			}
		}

		// Another mod has switched the collector off for a while -- a save-load speed-up holds it
		// off for the whole load, and OniPerf's fast loader for the frames after it too. Under
		// `Mode.Disabled` Unity ignores `GC.Collect()`, so a collection forced now would be timed at
		// ~0 ms, and that reading would reach the pause gate as a cost it never had. So nothing is
		// forced while it is off; the game's own collection when the hold ends clears the window.
		private static bool Held()
		{
			return UnityEngine.Scripting.GarbageCollector.GCMode != UnityEngine.Scripting.GarbageCollector.Mode.Enabled;
		}

		// The freeze itself, timed. Every number the pause gate reads comes through here.
		private double Collect()
		{
			var clock = Stopwatch.StartNew();
			GC.Collect();
			clock.Stop();
			lastBusyMs = clock.Elapsed.TotalMilliseconds;
			collections++;
			policy.NoteOurCollection(GC.CollectionCount(0), lastBusyMs);
			Publish();
			return lastBusyMs;
		}

		private void Report(string message)
		{
			Report(message, false);
		}

		// `budgetFlipped`: the pause budget has just changed its answer (or given its first in this
		// world). That line is neither collapsed nor capped, because it is the one a bug report
		// needs and the cap is reached long before it in a real game.
		private void Report(string message, bool budgetFlipped)
		{
			if (message == lastMessage && !budgetFlipped)
			{
				repeats++;
				return;
			}
			if (repeats > 0)
			{
				Emit("the decision above was taken " + repeats + " more time(s)", false);
				repeats = 0;
			}
			lastMessage = message;
			Emit(message, budgetFlipped);
		}

		private void Emit(string message, bool pastCap)
		{
			if (logged >= MaxLoggedDecisions)
			{
				if (pastCap)
					Log.Line(message + "  (past the " + MaxLoggedDecisions
						+ "-line cap: the pause budget changed its answer)");
				return;
			}
			logged++;
			Log.Line(message + (logged == MaxLoggedDecisions
				? "  (that is " + MaxLoggedDecisions + " lines; further decisions are not logged, "
					+ "except a change in the pause budget's answer)"
				: ""));
		}

		// What the watermark tooltip and the sentinel's own lines say about the timing half.
		private void Publish()
		{
			string triggers = config.CollectOnFocusLoss
				? (config.CollectOnPause ? "collect on focus loss and pause" : "collect on focus loss")
				: (config.CollectOnPause ? "collect on pause" : "armed but no trigger enabled");
			GcRuntimeInfo.Policy = triggers
				+ (config.CollectOnFocusLoss || config.CollectOnPause
					? " (floor " + config.FloorSeconds.ToString("0.0") + " s, fraction "
						+ config.Fraction.ToString("0.00")
						+ (config.CollectOnPause
							? ", pause budget " + config.PauseBudgetMs.ToString("0") + " ms"
							: string.Empty) + ")"
					: string.Empty)
				+ (collections > 0
					? ": " + collections + " forced so far, last " + lastBusyMs.ToString("0") + " ms"
					: ": none forced yet");
		}
	}
}
