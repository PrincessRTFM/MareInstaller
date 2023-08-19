namespace PrincessRTFM.MareInstaller;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using PrincessRTFM.LibRTFM.Core.Util;

public partial class ConfigUpdater: ConsoleWorkerModule {
	private const string
		repoListNode = "ThirdRepoList",
		arrayNode = "$values",
		repoUrlNode = "Url",
		repoActiveNode = "IsEnabled";

	private readonly HashSet<string> repos;
	public int Count => this.repos.Count;

	public override bool CanRun => this.repos.Count > 0 && File.Exists(Core.DalamudFile);

	public ConfigUpdater(IEnumerable<string> urls) {
		this.repos = new(urls, Core.caselessStringComparer);
	}

	public override bool Run() {
		string noun = "URL" + (this.Count == 1 ? "" : "s");
		this.Println($"Checking for {this.Count} third-party plugin repository {noun}");
		// load JSON
		string json = File.ReadAllText(Core.DalamudFile);
		JsonObject root = JsonNode.Parse(json, null, new() {
			AllowTrailingCommas = true,
			CommentHandling = JsonCommentHandling.Skip,
		}) as JsonObject ?? throw new NullReferenceException($"JSON library returned null from parse operation");
		// extract list from JsonArray at .["ThirdRepoList"]["$values"][]
		JsonArray repoArray = root[repoListNode]?[arrayNode] as JsonArray ?? throw new KeyNotFoundException("cannot find third-party repo list in dalamud config");
		List<JsonObject> repos = repoArray.Cast<JsonObject>().ToList();
		repoArray.Clear();
		this.Println($"{repos.Count} currently defined");
		// pass through cleanUrls(), dedupeUrls(), and addUrls()
		this.cleanUrls(repos);
		this.dedupeUrls(repos);
		this.addUrls(repos);
		// create new JsonArray from the list and insert it back into the original's place
		root[repoListNode]![arrayNode] = new JsonArray(repos.ToArray());
		// save JSON
		File.WriteAllText(Core.DalamudFile, root.ToJsonString(new() { WriteIndented = true }));
		return true;
	}

	private void cleanUrls(List<JsonObject> repoContainer) {
		// we can convert URLs like «https://github.com/PrincessRTFM/MyDalamudPlugins/raw/master/pluginmaster.json» into their redirected form
		// github sends a 302 Found response that _always_ points instead to a URL of the form «https://raw.githubusercontent.com/PrincessRTFM/MyDalamudPlugins/master/pluginmaster.json»
		// by using that directly, we can skip an intermediate request, and it seems to be easily done by regex
		Regex indirectGithubUrl = githubRedirectionUrlPattern();
		foreach (JsonObject repo in repoContainer) {
			string url = repo[repoUrlNode]?.GetValue<string>() ?? throw new KeyNotFoundException($"Repo object doesn't have a {repoUrlNode} node");
			Match m = indirectGithubUrl.Match(url);
			if (m.Success) {
				// need to rewrite this URL
				string repoUser = m.Groups["username"].Value;
				string repoName = m.Groups["repository"].Value;
				string repoPath = m.Groups["path"].Value;
				string result = $"https://raw.githubusercontent.com/{repoUser}/{repoName}/{repoPath}";
				repo[repoUrlNode] = result;
				this.Println($"Normalised {result}");
				url = result;
			}
			// after that, we want to ensure that the repo is enabled if it's one of the ones we care about
			if (this.repos.Contains(url)) {
				repo[repoActiveNode] = true;
				this.Println($"Forcibly enabled repo {url}");
			}
		}
	}
	private void dedupeUrls(List<JsonObject> repoContainer) {
		int originalCount = repoContainer.Count;
		HashSet<string> seen = new(Core.caselessStringComparer);
		for (int i = 0; i < repoContainer.Count; ++i) {
			string url = repoContainer[i][repoUrlNode]!.GetValue<string>()!;
			if (seen.Contains(url)) {
				this.Println($"Removing duplicate repo {url}");
				repoContainer.RemoveAt(i);
				--i;
				continue;
			}
			seen.Add(url);
		}
		int removed = originalCount - seen.Count;
		string noun = "URL" + (removed == 1 ? "" : "s");
		this.Println($"Removed {removed} duplicate repository {noun}, leaving {seen.Count}");
	}
	private void addUrls(List<JsonObject> repoContainer) {
		HashSet<string> known = new(repoContainer.Select(o => o[repoUrlNode]!.GetValue<string>()), Core.caselessStringComparer);
		JsonObject? template = null;
		foreach (string repoUrl in this.repos) {
			if (!known.Contains(repoUrl)) {
				// have to construct a new entry entirely, in order to add this url
				template ??= repoContainer.Count >= 1
					// if there's at least one entry in the list, we can deep-copy that then modify the necessary values, which means it should (theoretically) always result in something valid
					? repoContainer[0]
					// if not, we have to manually construct the whole thing, which may require updates if dalamud ever changes things
					// at present (2023-08-19) there are four fields:
					// - `$type` = `"Dalamud.Configuration.ThirdPartyRepoSettings, Dalamud"`
					// - `Url` = `"<URL>"`
					// - `IsEnabled` = `true`
					// - `Name` = `null`
					// the `Name` property is confirmed to be currently unused by goat himself, but this may change at some point
					// however, until it's actually used, I'm leaving it as `null` in accordance with existing configurations
					: new JsonObject() {
						["$type"] = "Dalamud.Configuration.ThirdPartyRepoSettings, Dalamud",
						[repoUrlNode] = string.Empty,
						[repoActiveNode] = true,
						["Name"] = null,
					};
				JsonObject injection = JsonNode.Parse(template.ToJsonString(), null, new() {
					AllowTrailingCommas = true,
					CommentHandling = JsonCommentHandling.Skip,
				}) as JsonObject ?? throw new NullReferenceException($"Failed to clone template node for repo URL insertion");
				injection[repoActiveNode] = true;
				injection[repoUrlNode] = repoUrl;
				repoContainer.Add(injection);
				known.Add(repoUrl);
				this.Println($"Added repo {repoUrl} to list");
			}
		}
	}

	[GeneratedRegex(@"^https?://(www\.)?github\.com/(?<username>[^/]+)/(?<repository>[^/]+)/raw/(?<path>.*)$", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture | RegexOptions.Compiled)]
	private static partial Regex githubRedirectionUrlPattern();
}
