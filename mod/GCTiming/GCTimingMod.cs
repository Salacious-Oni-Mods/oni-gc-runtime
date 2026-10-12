using System;
using System.Reflection;
using HarmonyLib;
using KMod;

// The GC Timing mod: the timing policy and the runtime report, in one mod.
//
// The timing policy (FocusCollector.cs, CollectPolicy.cs, Config.cs). The game stays on Unity's
// automatic GC mode -- no `GCMode` is set, so nothing has to be restored and no heap is held
// back -- and one collection is forced when the window LOSES FOCUS, if enough play has happened
// since the last collection to be worth it. A collection zeroes the allocation counter that
// triggers the next one, so that collection is not extra work: it is the one the game was about
// to run anyway, moved to a moment nobody is watching. Collecting on pause is off by default.
//
// The runtime report (GcRuntimeInfo.cs, Watermark.cs). It says which Mono runtime the process is
// on, in Player.log at load and on the build watermark from the main menu onward, so a player who
// installed the parallel-marking runtime can see whether it took.
//
// It changes no gameplay. It patches ONE method, the cosmetic watermark. With only this mod
// installed and the stock runtime in place, a game in which the window is never unfocused is
// exactly vanilla.
//
// Not called: base.OnLoad, which is harmony.PatchAll(assembly). Every patch here is applied by
// hand and wrapped, so a method the game renames costs a warning line instead of an exception on
// the load path.
namespace GCTiming
{
	public sealed class GCTimingMod : UserMod2
	{
		/// <Version> from the project, plus the commit the SDK stamps after '+', shortened to
		/// seven characters. The SDK does not mark a dirty tree.
		internal static string Version
		{
			get
			{
				var a = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
					typeof(GCTimingMod).Assembly, typeof(AssemblyInformationalVersionAttribute));
				string v = a != null ? a.InformationalVersion
					: typeof(GCTimingMod).Assembly.GetName().Version.ToString();
				int plus = v.IndexOf('+');
				return plus < 0 || v.Length < plus + 8 ? v : v.Substring(0, plus + 8);
			}
		}

		public override void OnLoad(Harmony harmony)
		{
			Log.Line(Version + " loaded. It forces a collection when the window loses focus, and"
				+ " reports which GC runtime is in place. Nothing else changes.");
			// Armed before the runtime lines are printed, because GcRuntimeInfo's own "GC timing"
			// line reports what this returned.
			Config config = Config.Load();
			Log.Line("timing: " + FocusCollector.Install(config));
			Log.Line("config: " + config.Describe());
			foreach (string line in GcRuntimeInfo.Lines())
				Log.Line(line);
			Watermark.Install(harmony);
		}
	}
}
