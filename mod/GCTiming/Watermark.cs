using System;
using HarmonyLib;
using UnityEngine;

// The build watermark says which GC runtime is active.
//
// Why the watermark. A player who ran the installer needs one place to check that it took, and
// the watermark is the only piece of UI that is on screen in every scene from the main menu
// onward. A screenshot or a bug report then carries the answer without anybody asking for
// Player.log.
//
// What it costs. One Harmony postfix on BuildWatermark.RefreshText, which appends " | GC <label>"
// to the label that shows the changelist and the DLC letters, and fills in the tooltip the game
// leaves empty (RefreshText calls ClearMultiStringTooltip on this branch). Nothing else in the
// game is patched or replaced, and no gameplay changes. If the patch cannot be applied the mod
// still loads and still logs: a cosmetic label must never be able to take a mod down.
//
// Composing with other watermark stamps. Another mod may rewrite the same line with a "| GC "
// field of its own. Whichever postfix runs second sees the other's work: if the field is already
// there this one appends nothing and leaves the tooltip alone, and if the other runs after ours it
// replaces the whole line, GC field included. Either order ends with exactly one GC field and no
// fight over the tooltip.
namespace GCTiming
{
	internal static class Watermark
	{
		// The separator AND the guard: if the line already contains this, something else (another
		// mod, or a second call of our own) has already said it.
		private const string Field = " | GC ";

		private static bool installed;
		private static bool warned;
		private static bool reported;

		internal static void Install(Harmony harmony)
		{
			if (installed || harmony == null)
				return;
			try
			{
				var target = AccessTools.Method(typeof(BuildWatermark), "RefreshText");
				if (target == null)
				{
					Log.Warn("BuildWatermark.RefreshText not found; the watermark is left alone"
						+ " (the sentinel's log lines above still stand)");
					return;
				}
				harmony.Patch(target, null,
					new HarmonyMethod(AccessTools.Method(typeof(Watermark), "Restamp")));
				installed = true;
			}
			catch (Exception e)
			{
				Log.Warn("could not patch the watermark: " + e);
			}
		}

		private static void Restamp(BuildWatermark __instance)
		{
			try
			{
				if (__instance == null || __instance.textDisplay == null)
					return;
				string text = __instance.textDisplay.text;
				if (string.IsNullOrEmpty(text))
					return;
				if (text.Contains(Field))
				{
					// Something else already answers the question on this line. Reported once so
					// the log can tell "stood down" from "never ran", which look the same on screen.
					Report("a GC field is already on the line, so the runtime report left it and the"
						+ " tooltip alone: \"" + text + "\"");
					return;
				}
				string line = text + Field + GcRuntimeInfo.Label;
				string tooltip = Tooltip();
				__instance.textDisplay.SetText(line);
				if (__instance.toolTip != null)
					__instance.toolTip.SetSimpleTooltip(tooltip);
				// The tooltip needs a mouse to be seen, so it is logged too, which puts it in a
				// player's log and in any bug report that carries one.
				Report("watermark: \"" + line + "\"" + (__instance.toolTip != null
					? "\ntooltip:\n" + tooltip : "  (no ToolTip component on the watermark)"));
			}
			catch (Exception e)
			{
				if (!warned)
				{
					warned = true;
					Log.Warn("could not stamp the watermark: " + e);
				}
			}
		}

		// Once per game launch: the watermark refreshes on every spawn, and a line per scene load says
		// nothing new.
		private static void Report(string message)
		{
			if (reported)
				return;
			reported = true;
			Log.Line(message);
		}

		private static string Tooltip()
		{
			var s = new System.Text.StringBuilder();
			s.Append("GC Timing ").Append(GCTimingMod.Version);
			foreach (string line in GcRuntimeInfo.Lines())
				s.Append('\n').Append(line);
			return s.ToString();
		}
	}
}
