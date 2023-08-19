namespace PrincessRTFM.MareInstaller;

public readonly struct PluginRepoData {
	public string Name { get; init; }
	public string Version { get; init; }
	public string RepoUrl { get; init; }
	public string DownloadUrl { get; init; }
}
