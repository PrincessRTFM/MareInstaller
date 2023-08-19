namespace PrincessRTFM.MareInstaller;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

using PrincessRTFM.LibRTFM.Core.Util;

public class Core: ConsoleWorkerModule {
	public const int ConsoleWidth = 180;
	public const int ConsoleHeight = 40;

	public const string ExeName = "ffxiv_dx11";
	public const string DalamudFolderName = "XIVLauncher";
	public const string DalamudConfigFile = "dalamudConfig.json";
	public const string XivLauncherDownloadUrl = "https://github.com/goatcorp/FFXIVQuickLauncher/releases/latest";
	public const string BugReportUrl = "https://github.com/PrincessRTFM/MareInstaller/issues/new/choose";

	public const string OfficialRepoUrl = "https://kamori.goats.dev/Plugin/PluginMaster";
	public const string SeaOfStarsRepoUrl = "https://raw.githubusercontent.com/Ottermandias/SeaOfStars/main/repo.json";

	internal static readonly StringComparer caselessStringComparer = StringComparer.OrdinalIgnoreCase;

	public static string Version { get; } = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion ?? "unknown";

	public static readonly ImmutableDictionary<string, string> Plugins = new Dictionary<string, string>() {
		{ "MareSynchronos", SeaOfStarsRepoUrl },
		{ "Penumbra", SeaOfStarsRepoUrl },
		{ "Glamourer", SeaOfStarsRepoUrl },
		{ "SimpleHeels", SeaOfStarsRepoUrl },
		{ "CustomizePlus", SeaOfStarsRepoUrl },
		{ "PalettePlus", SeaOfStarsRepoUrl },
		{ "Honorific", OfficialRepoUrl },
	}.ToImmutableDictionary();
	public static readonly ImmutableArray<string> AllRepositories = new string[] { OfficialRepoUrl }
		.Concat(Plugins.Values)
		.Distinct(caselessStringComparer)
		.ToImmutableArray();
	public static readonly ImmutableArray<string> CustomRepositories = AllRepositories
		.Where(u => !caselessStringComparer.Equals(u, OfficialRepoUrl))
		.ToImmutableArray();

	// The real paths, which are used in release builds to operate on, and in debug builds to clone to the fake ones
	public static readonly string RealDalamudPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), DalamudFolderName);
	public static readonly string RealDalamudFile = Path.Combine(RealDalamudPath, DalamudConfigFile);

	// The fake paths, used in debug builds to simulate things without touching the real files
	public static readonly string FakeDalamudPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Environment.CurrentDirectory, DalamudFolderName);
	public static readonly string FakeDalamudFile = Path.Combine(FakeDalamudPath, DalamudConfigFile);

#if DEBUG
	/// <summary>
	/// DEBUG BUILD - this a copy of <see cref="FakeDalamudPath"/>
	/// </summary>
	public static readonly string DalamudPath = FakeDalamudPath;
	/// <summary>
	/// DEBUG BUILD - this a copy of <see cref="FakeDalamudFile"/>
	/// </summary>
	public static readonly string DalamudFile = FakeDalamudFile;
#else
	/// <summary>
	/// RELEASE BUILD - this is a copy of <see cref="RealDalamudPath"/>
	/// </summary>
	public static readonly string DalamudPath = RealDalamudPath;
	/// <summary>
	/// RELEASE BUILD - this is a copy of <see cref="RealDalamudFile"/>
	/// </summary>
	public static readonly string DalamudFile = RealDalamudFile;
#endif
	public static readonly string PluginPath = Path.Combine(DalamudPath, "installedPlugins");

	public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

	public override bool CanRun => Process.GetProcessesByName(ExeName).Length == 0;

	public override bool Run() {
		ConfigUpdater repoInjector = new(CustomRepositories);
		if (!repoInjector.CanRun) {
			this.Println("Cannot run Dalamud config updater, precondition failed.");
			return false;
		}
		if (!repoInjector.Run()) {
			this.Println("Dalamud config updater failed, aborting.");
			return false;
		}
		this.Println();
		foreach (string repoUrl in AllRepositories) {
			this.Println($"Downloading plugin repo {repoUrl}");
			PluginDownloader.LoadRepo(repoUrl);
		}
		this.Println();
		PluginDownloader[] downloaders = Plugins
			.Select(pair => new PluginDownloader(pair.Key, pair.Value))
			.ToArray();
		int failed = 0;
		foreach (PluginDownloader download in downloaders) {
			try {
				if (!download.Run()) {
					++failed;
				}
			}
			catch (Exception top) {
				++failed;
				this.Println($"An exception occurred:");
				Exception? ex = top;
				int depth = 0;
				while (ex is not null) {
					string leader = string.Join("", Enumerable.Repeat("   ", ++depth));
					this.Println(leader + $"{ex.GetType().Name}: {ex.Message}");
					if (ex.TargetSite is not null)
						this.Println(leader + $" at {describeMethod(ex.TargetSite)}");
					ex = ex.InnerException;
				}
			}
			finally {
				this.Println();
			}
		}
		int completed = downloaders.Length - failed;
		string nounFailed = "download" + (failed == 1 ? "" : "s");
		string nounCompleted = "download" + (completed == 1 ? "" : "s");
		this.Println($"{completed} {nounCompleted} completed successfully, {failed} {nounFailed} failed.");
		return failed == 0;
	}

	public static void Main(string[] argv) {
		Console.CursorVisible = false;
#if DEBUG
		// in debug builds, the console window is always forced to resize, to test how it will look when necessary
		int curWidth = 0;
		int curHeight = 0;
#else
		// in release builds, the console only resizes when actually necessary
		int curWidth = Console.WindowWidth;
		int curHeight = Console.WindowHeight;
#endif
		int minWidth = Math.Min(Console.LargestWindowWidth, ConsoleWidth);
		int minHeight = Math.Min(Console.LargestWindowHeight, ConsoleHeight);
		int setWidth = Math.Max(curWidth, minWidth);
		int setHeight = Math.Max(curHeight, minHeight);
		Console.BufferWidth = Console.WindowWidth = setWidth;
		Console.BufferHeight = Console.WindowHeight = setHeight;
		Core core = new();
		core.Println($"Initialising automatic Mare Syncronos plugin installer v{Version}...");
#if DEBUG
		core.Println($"Set console window to {setWidth}x{setHeight}");
#endif
		string[] gameDetectedErrorLines = new[] {
			$"{ExeName}.exe is currently running.",
#if DEBUG
			"This would normally prevent this tool from operating, but this is a debug build, and only simulates the effects anyway.",
			"This message is printed only to validate that the game check is functional.",
			"",
#else
			"This tool cannot run while Dalamud is loaded, as it needs to modify Dalamud's configuration files.",
			"Unfortunately, it cannot tell if Dalamud is loaded into the game or not.",
			"In order to be sure that Dalamud is not running, you must close the game before using this tool.",
#endif
		};
		string[] dalamudMissingErrorLines = new[] {
			$"XIVLauncher/Dalamud does not appear to be installed.",
			"In order to use plugins like Mare Synchronos, you must run FFXIV with Dalamud, via XIVLauncher.",
			"Until you have XIVLauncher/Dalamud installed, this tool cannot run.",
			"Please install XIVLauncher and launch the game at least once through it, then rerun this tool.",
		};
		core.Println();
		if (!core.CanRun) {
			foreach (string line in gameDetectedErrorLines)
				core.Println(line, line.Length > 0);
		}
#if !DEBUG
		// in a release build, these two conditionals are linked because if the above fails, then _everything_ fails
		// in a debug build, we don't actually care if the game is running or not, so the `else` is omitted
		else
#endif
		// however, if the real file doesn't exist, then dalamud doesn't seem to be installed, which means nothing will do any good in the first place
		// also, debug builds won't have a source to copy from for the debugging file
		if (!File.Exists(RealDalamudFile)) {
			foreach (string line in dalamudMissingErrorLines)
				core.Println(line, line.Length > 0);
			core.Println();
			if (core.Confirm("Would you like to open the XIVLauncher download page now?") is true)
				OpenUrl(XivLauncherDownloadUrl);
		}
		else {
#if DEBUG
			// debug builds don't actually touch the real Dalamud's things, but we do need a copy of the real thing to simulate on
			// if there's already one in place, just use that (the user may have modified it for testing), but otherwise grab the real thing
			if (!File.Exists(FakeDalamudFile)) {
				Directory.CreateDirectory(FakeDalamudPath);
				try {
					File.Copy(RealDalamudFile, FakeDalamudFile, false);
				}
				catch (IOException) { }
			}
#endif
			Directory.CreateDirectory(PluginPath);
			bool success = false;
			try {
				success = core.Run();
			}
			catch (Exception e) {
				core.Println();
				core.Println($"{e.GetType().FullName}: {e.Message}", false);
				MethodBase? src = e.TargetSite;
				if (src is not null) {
					core.Println($"   at {describeMethod(src)}", false);
				}
			}
			core.Println();
			if (success) {
				core.Println("Mare Synchronos and dependencies have been installed.");
				core.Println("You still need to configure them yourself, most importantly Mare by registering an ID.");
				core.Println("You will then need to manually pair with any users you wish to.");
			}
			else {
				core.Println("Installation failed. Please either save or screenshot this log, and send it to the developer.");
				if (!string.IsNullOrWhiteSpace(BugReportUrl) && core.Confirm("Would you like to open the bug report page now?") is true)
					OpenUrl(BugReportUrl);
			}
		}
		core.Println();
		core.Pause("Press any key to exit.");
		Console.CursorVisible = true;
	}

	internal static string describeMethod(MethodBase method) {
		string name = $"{method.DeclaringType?.FullName ?? "<unknown>"}.{method.Name}";
		string args = string.Join(
			", ",
			method.GetParameters()
				.Select(p => $"{p.ParameterType}{(p.HasDefaultValue ? "?" : "")} {p.Name}")
		);
		return $"{name}({args})";
	}
}
