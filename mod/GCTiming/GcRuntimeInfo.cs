using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

// Which Mono runtime is this process ACTUALLY running on?
//
// Why it exists. The parallel-marking runtime replaces
// MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll. Nothing in the game says whether that
// replacement took. A player who installed it, a Steam "verify integrity of game files" that
// quietly put the shipped file back, an install that landed while the game was already running,
// and a bug report from any of them all look identical from inside. This answers it from the
// running process, so the player and the log both know which runtime is active.
//
// Self-contained: it depends on no other mod and needs nothing but kernel32 and the file on disk.
//
//   1. oni_gc_version, an export only the parallel-marking runtime has (tools/build-runtime.sh
//      writes the string it returns: "<patches/<set>/VERSION>-<set>+<commit>", with '*' when the
//      tree was dirty). Looked up with GetProcAddress on the ALREADY LOADED module, so nothing
//      is loaded here and a stock runtime costs no exception.
//   2. the file's SHA-256, compared with the one the game shipped. STOCK is decided by the hash,
//      not by the missing export: other mods ship modified runtimes too, and "not this one" is not
//      the same claim as "Unity's". A game update that ships a new runtime therefore reads as
//      unknown with its hash until ShippedSha256 is updated, which is the honest answer.
//   3. the ASCII string "oni_gc_version" in the file on disk, present in every build from
//      patches/ and in neither the shipped DLL nor an unchanged rebuild. If the loaded module has
//      no export but the file has the string, the install landed after this process started: the
//      answer for the player is "restart the game", not "it failed".
//
// Marker threads are the fourth probe and the only behavioural one. The patches name every
// parallel marker "BDWGC marker", so a run that reports a version with 0 markers has the DLL in
// place and its mark loop running single-threaded (GC_MARKERS=1, say), which costs full price.
// The threads exist before the first collection, so the count is meaningful at load.
//
// Read-only, and it never throws out: a cosmetic label must not be able to take a mod down.
// One GetProcAddress, one pass over the file that hashes and scans at the same time, one thread
// snapshot.
namespace GCTiming
{
	internal static class GcRuntimeInfo
	{
		/// SHA-256 of the mono-2.0-bdwgc.dll the game shipped with build 744825 (md5
		/// 2228ab08758aede23c069e769abe8a7e), the runtime the installer replaces.
		internal const string ShippedSha256 = "ea73d85a6ee0d776ebed64d101e6e8b20f132b658d969c137454725d94561092";
		internal const string ShippedBuild = "744825";

		private const string RuntimeModule = "mono-2.0-bdwgc.dll";
		private const string Export = "oni_gc_version";
		private const string MarkerThreadName = "BDWGC marker";

		private static bool queried;
		private static string version;
		private static string sha256;
		private static string modulePath;
		private static long fileBytes = -1;
		private static double readMs;
		private static bool fileHasMarker;
		private static string failure;

		/// What the timing policy is doing, for the log line and the tooltip. The policy sets this
		/// when it is armed.
		internal static string Policy = "not armed";

		/// The runtime's self-reported version, or null when the loaded module has no
		/// oni_gc_version export.
		internal static string Version { get { Query(); return version; } }

		/// SHA-256 of the runtime file on disk, or null if it could not be read.
		internal static string FileSha256 { get { Query(); return sha256; } }

		/// Full path of the loaded runtime, or null.
		internal static string ModulePath { get { Query(); return modulePath; } }

		/// True when the loaded module is byte-identical to the one ONI shipped.
		internal static bool IsStock { get { Query(); return version == null && sha256 == ShippedSha256; } }

		/// True when this process is NOT on the parallel-marking runtime but the file on disk is one: the
		/// install landed after startup, and a restart is all that is missing.
		internal static bool RestartPending { get { Query(); return version == null && fileHasMarker; } }

		/// Why detection could not finish, or null when it did.
		internal static string Failure { get { Query(); return failure; } }

		/// The short form for a one-line label: the runtime's version without its +commit (keeping a
		/// dirty '*'), STOCK, STOCK+RESTART, or '?#' and seven hex of the file's hash.
		internal static string Label
		{
			get
			{
				Query();
				if (version != null)
				{
					int plus = version.IndexOf('+');
					return plus < 0 ? version
						: version.Substring(0, plus) + (version.EndsWith("*") ? "*" : string.Empty);
				}
				if (sha256 == null)
					return "?";
				if (fileHasMarker)
					return sha256 == ShippedSha256 ? "STOCK+RESTART" : "?+RESTART";
				return sha256 == ShippedSha256 ? "STOCK" : "?#" + sha256.Substring(0, 7);
			}
		}

		/// The long form, one item per line, for a tooltip or the log. Every line is a fact about
		/// this process, not a claim about what the player meant to install.
		internal static string[] Lines()
		{
			Query();
			var lines = new System.Collections.Generic.List<string>();
			if (version != null)
				lines.Add("GC runtime: parallel marking " + version);
			else if (sha256 == null)
				lines.Add("GC runtime: UNIDENTIFIED (" + (failure ?? "no reason recorded") + ")");
			else if (sha256 == ShippedSha256)
				lines.Add("GC runtime: STOCK, the one the game shipped (build " + ShippedBuild + ")");
			else
				lines.Add("GC runtime: UNKNOWN, neither the parallel-marking runtime nor the file the game shipped");

			if (RestartPending)
				lines.Add("  the parallel-marking runtime IS installed on disk but this process did not load it:"
					+ " restart the game");
			else if (version == null && sha256 == ShippedSha256)
				lines.Add("  the parallel-marking runtime is not installed, so collections cost full price");

			if (version != null)
			{
				int markers, threads;
				string why;
				if (Threads(out markers, out threads, out why))
					lines.Add(markers > 0
						? "  parallel marking: " + markers + " marker threads (of " + threads + " in the process)"
						: "  parallel marking: NO marker threads, so the mark loop is single-threaded"
							+ " (GC_MARKERS=1?)");
				else
					lines.Add("  parallel marking: marker threads not countable (" + why + ")");
			}
			if (sha256 != null)
				lines.Add("  file: sha256 " + sha256.Substring(0, 16) + "..."
					+ (fileBytes > 0 ? "  " + (fileBytes / 1024 / 1024) + " MB" : string.Empty)
					+ ", read in " + readMs.ToString("F0") + " ms");
			if (modulePath != null)
				lines.Add("  " + modulePath);
			lines.Add("GC timing: " + Policy);
			return lines.ToArray();
		}

		private static void Query()
		{
			if (queried)
				return;
			queried = true;
			try
			{
				IntPtr module = GetModuleHandleW(RuntimeModule);
				if (module == IntPtr.Zero)
				{
					// Not reachable in the game, which runs on this DLL. It would mean a Mono
					// built with a different module name.
					failure = RuntimeModule + " is not loaded in this process";
					return;
				}
				version = CallVersion(module);
				modulePath = ModuleFile(module);
				if (modulePath == null || !File.Exists(modulePath))
				{
					failure = "the loaded module's path could not be read";
					return;
				}
				Scan(modulePath, out sha256, out fileHasMarker, out fileBytes, out readMs);
			}
			catch (Exception e)
			{
				failure = e.GetType().Name + ": " + e.Message;
			}
		}

		/// Hashes a runtime file and looks for the marker string in it, in ONE pass over the
		/// bytes. Separate from the loaded-module probes on purpose: it answers "is the file on
		/// disk a build from patches/", which is a different question from "is this process
		/// running one", and it is what detects the restart-pending state.
		///
		/// The needle can straddle a chunk boundary, so the last needle-1 bytes of each chunk are
		/// carried into the next search window.
		internal static void Scan(string path, out string hash, out bool hasMarker, out long bytes,
			out double milliseconds)
		{
			var watch = Stopwatch.StartNew();
			hash = null;
			hasMarker = false;
			byte[] needle = Encoding.ASCII.GetBytes(Export);
			int tail = needle.Length - 1;
			var buffer = new byte[64 * 1024];
			var window = new byte[buffer.Length + tail];
			int held = 0;
			long total = 0;
			// ReadWrite share: this process has the file mapped, and an installer may be writing
			// over it right now. A locked file must read as unidentified, never throw.
			using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
			using (SHA256 sha = SHA256.Create())
			{
				int n;
				while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
				{
					sha.TransformBlock(buffer, 0, n, null, 0);
					total += n;
					Buffer.BlockCopy(buffer, 0, window, held, n);
					int len = held + n;
					if (!hasMarker && Contains(window, len, needle))
						hasMarker = true;
					held = len < tail ? len : tail;
					if (held > 0)
						Buffer.BlockCopy(window, len - held, window, 0, held);
				}
				sha.TransformFinalBlock(new byte[0], 0, 0);
				hash = Hex(sha.Hash);
			}
			bytes = total;
			milliseconds = watch.Elapsed.TotalMilliseconds;
		}

		private static bool Contains(byte[] hay, int length, byte[] needle)
		{
			int last = length - needle.Length;
			for (int i = 0; i <= last; i++)
			{
				int j = 0;
				while (j < needle.Length && hay[i + j] == needle[j])
					j++;
				if (j == needle.Length)
					return true;
			}
			return false;
		}

		private static string Hex(byte[] bytes)
		{
			var s = new StringBuilder(bytes.Length * 2);
			for (int i = 0; i < bytes.Length; i++)
				s.Append(bytes[i].ToString("x2"));
			return s.ToString();
		}

		/// Counts this process's threads and how many of them are bdwgc markers. Always a fresh
		/// snapshot: it is cheap, and a cached count would outlive the thing it describes.
		internal static bool Threads(out int markers, out int threads, out string why)
		{
			markers = -1;
			threads = -1;
			why = null;
			IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
			if (snap == new IntPtr(-1))
			{
				why = "snapshot failed";
				return false;
			}
			try
			{
				uint me = GetCurrentProcessId();
				int m = 0;
				int t = 0;
				var e = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(THREADENTRY32)) };
				for (bool more = Thread32First(snap, ref e); more; more = Thread32Next(snap, ref e))
				{
					if (e.th32OwnerProcessID != me)
						continue;
					t++;
					IntPtr h = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, false, e.th32ThreadID);
					if (h == IntPtr.Zero)
						continue;
					try
					{
						IntPtr desc;
						if (GetThreadDescription(h, out desc) >= 0 && desc != IntPtr.Zero)
						{
							if (Marshal.PtrToStringUni(desc) == MarkerThreadName)
								m++;
							LocalFree(desc);
						}
					}
					finally
					{
						CloseHandle(h);
					}
				}
				markers = m;
				threads = t;
				return true;
			}
			catch (EntryPointNotFoundException)
			{
				why = "no GetThreadDescription before Windows 10 1607";
				return false;
			}
			finally
			{
				CloseHandle(snap);
			}
		}

		/// "markers=15 of 20 threads", or a reason it could not be counted.
		internal static string ThreadSummary()
		{
			int markers, threads;
			string why;
			return Threads(out markers, out threads, out why)
				? "markers=" + markers + " of " + threads + " threads"
				: "markers=? (" + why + ")";
		}

		private static string CallVersion(IntPtr module)
		{
			IntPtr fn = GetProcAddress(module, Export);
			if (fn == IntPtr.Zero)
				return null;
			var call = (VersionFn)Marshal.GetDelegateForFunctionPointer(fn, typeof(VersionFn));
			IntPtr s = call();
			// A string literal the DLL owns for the life of the process: copy it, never free it.
			return s == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(s);
		}

		private static string ModuleFile(IntPtr module)
		{
			var buffer = new char[1024];
			uint n = GetModuleFileNameW(module, buffer, (uint)buffer.Length);
			return n == 0 || n >= buffer.Length ? null : new string(buffer, 0, (int)n);
		}

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate IntPtr VersionFn();

		[StructLayout(LayoutKind.Sequential)]
		private struct THREADENTRY32
		{
			public uint dwSize;
			public uint cntUsage;
			public uint th32ThreadID;
			public uint th32OwnerProcessID;
			public int tpBasePri;
			public int tpDeltaPri;
			public uint dwFlags;
		}

		private const uint TH32CS_SNAPTHREAD = 0x4;
		private const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;

		[DllImport("kernel32", CharSet = CharSet.Unicode)]
		private static extern IntPtr GetModuleHandleW(string name);
		[DllImport("kernel32", CharSet = CharSet.Unicode)]
		private static extern uint GetModuleFileNameW(IntPtr module, [Out] char[] path, uint size);
		[DllImport("kernel32", CharSet = CharSet.Ansi, BestFitMapping = false)]
		private static extern IntPtr GetProcAddress(IntPtr module, string name);
		[DllImport("kernel32", SetLastError = true)]
		private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
		[DllImport("kernel32")]
		private static extern bool Thread32First(IntPtr snap, ref THREADENTRY32 e);
		[DllImport("kernel32")]
		private static extern bool Thread32Next(IntPtr snap, ref THREADENTRY32 e);
		[DllImport("kernel32")]
		private static extern IntPtr OpenThread(uint access, bool inherit, uint id);
		[DllImport("kernel32")]
		private static extern bool CloseHandle(IntPtr h);
		[DllImport("kernel32")]
		private static extern uint GetCurrentProcessId();
		[DllImport("kernel32")]
		private static extern IntPtr LocalFree(IntPtr p);
		[DllImport("kernel32")]
		private static extern int GetThreadDescription(IntPtr thread, out IntPtr description);
	}
}
