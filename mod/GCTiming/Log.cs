using UnityEngine;

// One prefix for everything this mod says, so a player's Player.log can be grepped for it and so
// the policy lines and the runtime report read as one voice.
namespace GCTiming
{
	internal static class Log
	{
		private const string Prefix = "[GCTiming] ";

		internal static void Line(string message)
		{
			Debug.Log(Prefix + message);
		}

		internal static void Warn(string message)
		{
			Debug.LogWarning(Prefix + message);
		}
	}
}
