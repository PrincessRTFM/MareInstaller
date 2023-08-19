namespace PrincessRTFM.MareInstaller;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using PrincessRTFM.LibRTFM.Core.Util;

public class PluginDownloader: ConsoleWorkerModule {

	public override string Label => $"{base.Label}: {this.PluginName}";

	public string RepoUrl { get; init; }
	public string PluginName { get; init; }

	private PluginRepoData? meta = null;
	public PluginRepoData? Metadata => this.meta ??= FindPlugin(this.PluginName);

	#region Caching
	// this is purely a minor optimisation to avoid iterating all PluginRepoData values to see if we've already acquired a given repo URL
	// the keys in pluginCache are the PLUGIN names, so there's no good place to put REPO urls... except with something like this
	internal static readonly HashSet<string> downloadedRepos = new(Core.caselessStringComparer);
	// ALL access to both this value (plugin cache) AND the repo cache above gets locked on THIS object!
	internal static readonly Dictionary<string, PluginRepoData> pluginCache = new(Core.caselessStringComparer);

	public static PluginRepoData? FindPlugin(string name) {
		lock (pluginCache) {
			return pluginCache.TryGetValue(name, out PluginRepoData plugin) ? plugin : null;
		}
	}
	public static bool HasPlugin(string name) => FindPlugin(name) is not null;
	#endregion

	#region Internet
	private static readonly HttpClient web = new();

	protected static async Task<Stream?> HttpGet(string url) {
		try {
			HttpResponseMessage response = await web.GetAsync(url);
			response.EnsureSuccessStatusCode();
			return response.Content.ReadAsStream();
		}
		catch (TaskCanceledException e) {
			if (e.InnerException is TimeoutException or null)
				throw new TimeoutException($"HTTP request timed out (>{web.Timeout.TotalSeconds}s)");
			throw; // unpossible
		}
	}
	#endregion

	public PluginDownloader(string name, string repo) {
		this.PluginName = name;
		this.RepoUrl = repo;
	}
	static PluginDownloader() {
		web.Timeout = TimeSpan.FromSeconds(30);

		List<ProductInfoHeaderValue> agents = new() {
			new("Dalamud-MareSynchronos-installer", Core.Version)
		};
		agents.AddRange(web.DefaultRequestHeaders.UserAgent);
		web.DefaultRequestHeaders.UserAgent.Clear();
		foreach (ProductInfoHeaderValue item in agents)
			web.DefaultRequestHeaders.UserAgent.Add(item);
		web.DefaultRequestHeaders.From = "PrincessRTFM";
		web.DefaultRequestHeaders.CacheControl = new() {
#if DEBUG
			// on a debug build, we don't care if it's stale, since it's all a dry run anyway
			MaxStale = true,
#else
			// on a release build, we want the current content
			MaxStale = false,
#endif
			NoTransform = true,
		};
	}

	public static void LoadRepo(string url) {
		lock (pluginCache) {
			if (downloadedRepos.Contains(url))
				return;
			downloadedRepos.Add(url);
			Task<Stream?> request = HttpGet(url);
			try {
				request.Wait();
			}
			catch (Exception e) {
				throw new HttpRequestException($"Failed to download repository {url}", e);
			}
			Stream body = request.Result!;
			if (body.CanSeek)
				body.Seek(0, SeekOrigin.Begin);
			using StreamReader reader = new(body);
			JsonArray repo = JsonNode.Parse(reader.ReadToEnd(), null, new() {
				AllowTrailingCommas = true,
				CommentHandling = JsonCommentHandling.Skip,
			}) as JsonArray ?? throw new NullReferenceException($"Failed to parse repo content");
			foreach (JsonNode? entry in repo) {
				if (entry is not JsonObject pluginEntry)
					continue;
				string pluginName = pluginEntry["InternalName"]?.GetValue<string>() ?? throw new NullReferenceException("Plugin entry in repo doesn't have an InternalName");
				string pluginUrl = pluginEntry["DownloadLinkInstall"]?.GetValue<string>() ?? throw new NullReferenceException("Plugin entry in repo doesn't have a DownloadLinkInstall");
				string pluginVersion = pluginEntry["AssemblyVersion"]?.GetValue<string>() ?? throw new NullReferenceException("Plugin entry in repo doesn't have an AssemblyVersion");
				if (pluginCache.ContainsKey(pluginName))
					continue;
				PluginRepoData plugin = new() {
					Name = pluginName,
					Version = pluginVersion,
					RepoUrl = url,
					DownloadUrl = pluginUrl
				};
				pluginCache.Add(pluginName, plugin);
			}
		}
	}
	public void LoadRepo() => LoadRepo(this.RepoUrl);

	public Stream DownloadPlugin(string name, string repoUrl) {
		LoadRepo(repoUrl);
		lock (pluginCache) {
			if (this.Metadata is null)
				throw new KeyNotFoundException($"cannot find {name} in {repoUrl}");
			this.Println($"Downloading plugin {this.Metadata!.Value.Name} from {this.Metadata!.Value.DownloadUrl}");
			Task<Stream?> request = HttpGet(this.Metadata!.Value.DownloadUrl);
			try {
				request.Wait();
			}
			catch (Exception e) {
				throw new HttpRequestException($"Failed to download {this.Metadata!.Value.Name} from {this.Metadata!.Value.DownloadUrl}", e);
			}
			Stream body = request.Result!;
			if (body.CanSeek)
				body.Seek(0, SeekOrigin.Begin);
			return body;
		}
	}
	public Stream DownloadPlugin() => this.DownloadPlugin(this.PluginName, this.RepoUrl);

	public override bool CanRun => Directory.Exists(Core.PluginPath);

	public override bool Run() {
		this.LoadRepo();
		string versionedInstallFolder = Path.TrimEndingDirectorySeparator(Path.Combine(Core.PluginPath, this.Metadata!.Value.Name, this.Metadata!.Value.Version));
		if (Directory.Exists(versionedInstallFolder)) {
			this.Println($"v{this.Metadata!.Value.Version} is already installed");
			return true;
		}
		Stream content = this.DownloadPlugin();
		using ZipArchive zip = new(content, ZipArchiveMode.Read, false);
		if (zip.Entries.Count == 0)
			throw new InvalidDataException($"Plugin archive for v{this.Metadata!.Value.Version} contains no files");
		this.Println($"Extracting plugin files for v{this.Metadata!.Value.Version} ({zip.Entries.Count} found)");
		Dictionary<string, ZipArchiveEntry> files = zip.Entries
			.Where(entry => {
				string target = Path.GetFullPath(Path.Combine(versionedInstallFolder, entry.FullName));
				return target.StartsWith(versionedInstallFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal)
					|| target.StartsWith(versionedInstallFolder + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
			})
			.ToDictionary(entry => Path.GetFullPath(Path.Combine(versionedInstallFolder, entry.FullName)));
		if (files.Count != zip.Entries.Count)
			throw new InvalidDataException($"Plugin archive for {this.PluginName} v{this.Metadata!.Value.Version} contains an entry somehow outside the install path");
		foreach (KeyValuePair<string, ZipArchiveEntry> file in files) {
			string folder = Path.GetDirectoryName(file.Key)!;
			try {
				Directory.CreateDirectory(folder);
				file.Value.ExtractToFile(file.Key, true);
			}
			catch {
				this.Println($"Cannot extract {file.Value.FullName} to disk, installation aborted");
				Directory.Delete(versionedInstallFolder, true);
				throw;
			}
		}
		return true;
	}
}
