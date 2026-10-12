using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

// The timing policy's settings, read from `config.json` beside the mod's DLL.
//
// WHY A FILE AND NOT AN OPTIONS SCREEN. An options library is another mod, and this mod works on a
// plain install with nothing else present. Three numbers and two switches do not justify a
// dependency, so they live in a file that is absent by default.
//
// WHY IT IS ABSENT BY DEFAULT AND NEVER WRITTEN. A mod folder is replaced wholesale by a Workshop
// update, so a file this mod writes there would be destroyed on the next update and a player
// would lose settings without being told. The defaults are in the code, the effective values are
// logged at load, and a player who wants to change them creates the file:
//
//   { "collectOnFocusLoss": true, "collectOnPause": false, "pauseBudgetMs": 80.0,
//     "floorSeconds": 5.0, "fraction": 0.5, "window": 8 }
//
// `collectOnPause` is off by default. Measured on the parallel-marking runtime, four 30-minute
// soaks alternating on and off: 50 collections in play with it on against 41 off (p = 0.40), the
// collection frame unchanged, and a third more GC work in total. A freeze spent on the paused
// screen that removes none from play is a cost with no return, so it is opt-in. With it off the
// load-end cost probe is skipped as well. When it is on, the pause trigger needs a MEASURED
// collection within `pauseBudgetMs`, and a collection on the game's own runtime is about 242 ms
// (CollectPolicy's header has the reasoning). Raising the budget past that is how a player says
// they would rather have the collection moved to the pause than left in play; it is not the
// default, because a quarter-second freeze on a paused screen is visible.
//
// Everything is optional: a key that is missing, misspelled or the wrong type keeps its default.
// A file that cannot be parsed at all costs one warning line and the defaults -- a bad config
// must never stop the mod loading, and least of all stop the game.
namespace GCTiming
{
	internal sealed class Config
	{
		internal bool CollectOnFocusLoss = true;
		internal bool CollectOnPause = false;
		internal double PauseBudgetMs = CollectPolicy.DefaultPauseBudgetMs;
		internal double FloorSeconds = CollectPolicy.DefaultFloorSeconds;
		internal double Fraction = CollectPolicy.DefaultFraction;
		internal int Window = CollectPolicy.DefaultWindow;

		/// Where the values came from, for the load line: the defaults, or the file plus
		/// whatever it was allowed to change.
		internal string Source = "defaults (no config.json)";

		// Clamps, so a typo in a hand-edited file cannot produce a policy that collects on every
		// alt-tab or never collects at all. Named constants because the log line prints them
		// when a value is clamped.
		private const double MinFloor = 0.5, MaxFloor = 600.0;
		private const double MinFraction = 0.05, MaxFraction = 4.0;
		private const int MinWindow = 2, MaxWindow = 64;
		// The upper bound is generous on purpose: a player who sets 5000 has asked for every
		// collection to land on a pause, and the log line says what they asked for. The lower
		// bound is not 0 -- a budget of 0 would be a disabled trigger written the confusing way,
		// and `collectOnPause: false` already says that.
		private const double MinBudget = 1.0, MaxBudget = 5000.0;

		// The shape of the file. Nullable, so "absent" and "present and 0" are different things.
		private sealed class FileShape
		{
			public bool? collectOnFocusLoss = null;
			public bool? collectOnPause = null;
			public double? pauseBudgetMs = null;
			public double? floorSeconds = null;
			public double? fraction = null;
			public int? window = null;
		}

		internal static Config Load()
		{
			var c = new Config();
			string path = null;
			try
			{
				string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
				if (string.IsNullOrEmpty(dir))
					return c;
				path = Path.Combine(dir, "config.json");
				if (!File.Exists(path))
					return c;
				var f = JsonConvert.DeserializeObject<FileShape>(File.ReadAllText(path));
				if (f == null)
					return c;
				string notes = "";
				if (f.collectOnFocusLoss.HasValue)
					c.CollectOnFocusLoss = f.collectOnFocusLoss.Value;
				if (f.collectOnPause.HasValue)
					c.CollectOnPause = f.collectOnPause.Value;
				if (f.pauseBudgetMs.HasValue)
					c.PauseBudgetMs = Clamp(f.pauseBudgetMs.Value, MinBudget, MaxBudget, "pauseBudgetMs", ref notes);
				if (f.floorSeconds.HasValue)
					c.FloorSeconds = Clamp(f.floorSeconds.Value, MinFloor, MaxFloor, "floorSeconds", ref notes);
				if (f.fraction.HasValue)
					c.Fraction = Clamp(f.fraction.Value, MinFraction, MaxFraction, "fraction", ref notes);
				if (f.window.HasValue)
					c.Window = (int)Clamp(f.window.Value, MinWindow, MaxWindow, "window", ref notes);
				c.Source = "config.json" + notes;
			}
			catch (Exception e)
			{
				Log.Warn("could not read " + (path ?? "config.json") + ", using the defaults: "
					+ e.GetType().Name + ": " + e.Message);
				return new Config { Source = "defaults (config.json could not be read)" };
			}
			return c;
		}

		private static double Clamp(double v, double lo, double hi, string name, ref string notes)
		{
			if (v >= lo && v <= hi)
				return v;
			double c = v < lo ? lo : hi;
			notes += " (" + name + " " + v + " clamped to " + c + ")";
			return c;
		}

		internal string Describe()
		{
			string triggers = CollectOnFocusLoss
				? (CollectOnPause ? "on focus loss and on pause" : "on focus loss")
				: (CollectOnPause ? "on pause only" : "no triggers armed");
			return triggers + ", floor " + FloorSeconds.ToString("0.0") + " s, fraction "
				+ Fraction.ToString("0.00") + ", window " + Window + ", pause budget "
				+ PauseBudgetMs.ToString("0") + " ms; from " + Source;
		}
	}
}
