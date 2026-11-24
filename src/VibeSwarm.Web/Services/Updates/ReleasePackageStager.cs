using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using VibeSwarm.Shared.Branding;

namespace VibeSwarm.Web.Services.Updates;

/// <summary>A problem the user can read: why an update was refused or stopped.</summary>
public sealed class UpdateException(string message) : Exception(message);

public interface IReleasePackageStager
{
	/// <summary>
	/// Downloads the release's Linux/macOS package, checks its SHA-256, extracts it into a fresh
	/// folder and checks it is the expected version for a runtime this host has, then moves it to
	/// <c>versions/&lt;version&gt;</c>. Returns that folder. Nothing outside <c>versions/</c> changes.
	/// </summary>
	Task<string> StageAsync(GitHubRelease release, ReleaseVersion version, InstallLayout layout,
		IProgress<int>? downloadProgress, CancellationToken cancellationToken);
}

public sealed class ReleasePackageStager : IReleasePackageStager
{
	private const long MaxPackageBytes = 1024L * 1024 * 1024;
	private const long MaxChecksumFileBytes = 64 * 1024;
	public const string ChecksumFileName = "SHA256SUMS";

	private readonly IHttpClientFactory _httpClientFactory;
	private readonly IInstalledRuntimeProvider _runtimes;
	private readonly ILogger<ReleasePackageStager> _logger;

	public ReleasePackageStager(IHttpClientFactory httpClientFactory, IInstalledRuntimeProvider runtimes, ILogger<ReleasePackageStager> logger)
	{
		_httpClientFactory = httpClientFactory;
		_runtimes = runtimes;
		_logger = logger;
	}

	/// <summary>The archive name release.yml publishes: <c>{PackageId}-{tag}.tar.gz</c>.</summary>
	public static string GetPackageAssetName(string tag) => $"{AppBranding.PackageId}-{tag}.tar.gz";

	public static GitHubReleaseAsset? FindPackageAsset(GitHubRelease release) =>
		release.Assets.FirstOrDefault(a => string.Equals(a.Name, GetPackageAssetName(release.TagName), StringComparison.Ordinal))
		?? release.Assets.FirstOrDefault(a =>
			a.Name.StartsWith($"{AppBranding.PackageId}-", StringComparison.Ordinal) &&
			a.Name.EndsWith(".tar.gz", StringComparison.Ordinal));

	public async Task<string> StageAsync(GitHubRelease release, ReleaseVersion version, InstallLayout layout,
		IProgress<int>? downloadProgress, CancellationToken cancellationToken)
	{
		var asset = FindPackageAsset(release)
			?? throw new UpdateException($"Release {release.TagName} has no Linux/macOS package ({GetPackageAssetName(release.TagName)}).");
		if (asset.Size <= 0 || asset.Size > MaxPackageBytes)
		{
			throw new UpdateException($"Release {release.TagName}'s package has an unexpected size ({asset.Size:N0} bytes).");
		}

		EnsureGitHubDownloadUrl(asset.BrowserDownloadUrl);
		var expectedHash = await GetExpectedSha256Async(release, asset, cancellationToken);

		Directory.CreateDirectory(layout.VersionsDirectory);
		EnsureFreeSpace(layout.VersionsDirectory, asset.Size);

		var stagingDirectory = Path.Combine(layout.VersionsDirectory, $"{InstallLayout.StagingPrefix}{version}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(stagingDirectory);
		try
		{
			var archivePath = Path.Combine(stagingDirectory, "package.tar.gz");
			var actualHash = await DownloadAsync(asset, archivePath, downloadProgress, cancellationToken);
			if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
			{
				throw new UpdateException($"The downloaded package doesn't match its published SHA-256, so it wasn't installed. Try again; if it keeps failing, report it.");
			}

			var extractDirectory = Path.Combine(stagingDirectory, "extract");
			await ExtractAsync(archivePath, extractDirectory, cancellationToken);
			File.Delete(archivePath);

			var packageRoot = FindAppDirectory(extractDirectory)
				?? throw new UpdateException("The package doesn't contain the app. It wasn't installed.");
			ValidatePackageVersion(packageRoot.EntryDll, version);
			await ValidateRuntimeAsync(packageRoot.RuntimeConfig, cancellationToken);

			var target = layout.GetVersionDirectory(version.ToString());
			if (Directory.Exists(target))
			{
				// A folder left by an earlier attempt that never ran; the running one is refused earlier.
				Directory.Delete(target, recursive: true);
			}

			Directory.Move(packageRoot.Directory, target);
			return target;
		}
		finally
		{
			TryDeleteDirectory(stagingDirectory);
		}
	}

	private static void EnsureGitHubDownloadUrl(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
			!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
		{
			throw new UpdateException("The release's download link doesn't point at github.com, so it wasn't used.");
		}
	}

	private async Task<string> GetExpectedSha256Async(GitHubRelease release, GitHubReleaseAsset asset, CancellationToken cancellationToken)
	{
		const string prefix = "sha256:";
		if (asset.Digest is { } digest && digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && IsSha256Hex(digest[prefix.Length..]))
		{
			return digest[prefix.Length..].ToLowerInvariant();
		}

		var checksumAsset = release.Assets.FirstOrDefault(a => string.Equals(a.Name, ChecksumFileName, StringComparison.Ordinal))
			?? throw new UpdateException($"Release {release.TagName} publishes no checksum, so its package can't be verified.");
		EnsureGitHubDownloadUrl(checksumAsset.BrowserDownloadUrl);

		var client = _httpClientFactory.CreateClient(GitHubReleaseClient.HttpClientName);
		using var response = await client.GetAsync(checksumAsset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			throw new UpdateException($"Couldn't download the release's checksums (HTTP {(int)response.StatusCode}).");
		}

		if (response.Content.Headers.ContentLength > MaxChecksumFileBytes)
		{
			throw new UpdateException("The release's checksum file is too large to be one.");
		}

		var text = await response.Content.ReadAsStringAsync(cancellationToken);
		var hash = ParseChecksumFile(text, asset.Name)
			?? throw new UpdateException($"The release's checksums don't list {asset.Name}, so it can't be verified.");
		return hash;
	}

	/// <summary>Reads <c>sha256sum</c> output: <c>&lt;hex&gt;  &lt;name&gt;</c> (or <c>*&lt;name&gt;</c> in binary mode).</summary>
	public static string? ParseChecksumFile(string text, string fileName)
	{
		foreach (var rawLine in text.Split('\n'))
		{
			var line = rawLine.Trim();
			var space = line.IndexOf(' ');
			if (space <= 0)
			{
				continue;
			}

			var hash = line[..space];
			var name = line[(space + 1)..].TrimStart(' ', '*');
			if (string.Equals(name, fileName, StringComparison.Ordinal) && IsSha256Hex(hash))
			{
				return hash.ToLowerInvariant();
			}
		}

		return null;
	}

	private static bool IsSha256Hex(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);

	private static void EnsureFreeSpace(string directory, long packageBytes)
	{
		try
		{
			var available = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
			// The archive, its extracted files (several times larger) and headroom for the database backup.
			if (available < packageBytes * 6)
			{
				throw new UpdateException($"Not enough free disk space for the update ({available / (1024 * 1024):N0} MB free).");
			}
		}
		catch (Exception ex) when (ex is not UpdateException)
		{
			// Some file systems can't report free space; the download fails cleanly if it runs out.
		}
	}

	private async Task<string> DownloadAsync(GitHubReleaseAsset asset, string path, IProgress<int>? progress, CancellationToken cancellationToken)
	{
		var client = _httpClientFactory.CreateClient(GitHubReleaseClient.HttpClientName);
		client.Timeout = Timeout.InfiniteTimeSpan;
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromMinutes(30));

		using var request = new HttpRequestMessage(HttpMethod.Get, asset.BrowserDownloadUrl);
		request.Headers.Accept.Clear();
		request.Headers.Accept.ParseAdd("application/octet-stream");
		using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
		if (!response.IsSuccessStatusCode)
		{
			throw new UpdateException($"Couldn't download the package (HTTP {(int)response.StatusCode}).");
		}

		await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
		await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

		var buffer = new byte[81920];
		long total = 0;
		var lastPercent = -1;
		int read;
		while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
		{
			total += read;
			if (total > asset.Size)
			{
				throw new UpdateException("The download is larger than the release says it is, so it was stopped.");
			}

			hash.AppendData(buffer, 0, read);
			await destination.WriteAsync(buffer.AsMemory(0, read), timeout.Token);

			var percent = (int)(total * 100 / asset.Size);
			if (percent != lastPercent)
			{
				lastPercent = percent;
				progress?.Report(percent);
			}
		}

		if (total != asset.Size)
		{
			throw new UpdateException("The download ended early. Try again.");
		}

		await destination.FlushAsync(timeout.Token);
		return Convert.ToHexStringLower(hash.GetHashAndReset());
	}

	/// <summary>Extracts regular files and folders only, each confined to <paramref name="destination"/>.</summary>
	public static async Task ExtractAsync(string archivePath, string destination, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(destination);
		var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;

		await using var file = File.OpenRead(archivePath);
		await using var gzip = new GZipStream(file, CompressionMode.Decompress);
		await using var reader = new TarReader(gzip);
		try
		{
			while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
			{
				if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes)
				{
					continue;
				}

				var name = entry.Name.Replace('\\', '/').TrimStart('/');
				if (name.Length == 0 || name == "." || name == "./")
				{
					continue;
				}

				var fullPath = Path.GetFullPath(Path.Combine(root, name));
				if (!fullPath.StartsWith(root, StringComparison.Ordinal) && fullPath + Path.DirectorySeparatorChar != root)
				{
					throw new UpdateException($"The package tries to write outside its folder ({entry.Name}). It wasn't installed.");
				}

				switch (entry.EntryType)
				{
					case TarEntryType.Directory:
						Directory.CreateDirectory(fullPath);
						break;
					case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
						Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
						await entry.ExtractToFileAsync(fullPath, overwrite: false, cancellationToken);
						break;
					default:
						throw new UpdateException($"The package holds an unexpected {entry.EntryType} entry ({entry.Name}). It wasn't installed.");
				}
			}
		}
		catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException)
		{
			throw new UpdateException("The package isn't a readable .tar.gz archive. It wasn't installed.");
		}
	}

	public sealed record AppDirectory(string Directory, string EntryDll, string RuntimeConfig);

	/// <summary>
	/// The folder holding the web app: the one whose <c>*.runtimeconfig.json</c> asks for
	/// ASP.NET Core (the Blazor client's doesn't). The launcher picks the entry dll the same way.
	/// </summary>
	public static AppDirectory? FindAppDirectory(string extractDirectory)
	{
		var candidates = new[] { extractDirectory }
			.Concat(Directory.EnumerateDirectories(extractDirectory))
			.ToList();
		foreach (var directory in candidates)
		{
			foreach (var runtimeConfig in Directory.EnumerateFiles(directory, "*.runtimeconfig.json"))
			{
				if (!File.ReadAllText(runtimeConfig).Contains("\"Microsoft.AspNetCore.App\"", StringComparison.Ordinal))
				{
					continue;
				}

				var entryDll = runtimeConfig[..^".runtimeconfig.json".Length] + ".dll";
				if (File.Exists(entryDll))
				{
					return new AppDirectory(directory, entryDll, runtimeConfig);
				}
			}
		}

		return null;
	}

	private static void ValidatePackageVersion(string entryDll, ReleaseVersion expected)
	{
		var productVersion = FileVersionInfo.GetVersionInfo(entryDll).ProductVersion;
		if (!ReleaseVersion.TryParse(productVersion, out var packaged) || packaged.CompareTo(expected) != 0)
		{
			throw new UpdateException($"The package is version {productVersion ?? "unknown"}, not {expected}. It wasn't installed.");
		}
	}

	private async Task ValidateRuntimeAsync(string runtimeConfigPath, CancellationToken cancellationToken)
	{
		var required = ReadRequiredFrameworks(await File.ReadAllTextAsync(runtimeConfigPath, cancellationToken));
		if (required.Count == 0)
		{
			return;
		}

		var installed = await _runtimes.GetInstalledRuntimesAsync(cancellationToken);
		if (installed is null)
		{
			_logger.LogWarning("Couldn't list the installed .NET runtimes; installing the update without checking them.");
			return;
		}

		foreach (var (name, version) in required)
		{
			if (!installed.Any(r => string.Equals(r.Name, name, StringComparison.Ordinal) && SatisfiesRollForward(r.Version, version)))
			{
				throw new UpdateException(
					$"This release needs {name} {version.Major}.{version.Minor}, which isn't installed. Re-run install.sh to add it, then update.");
			}
		}
	}

	/// <summary>The frameworks a runtimeconfig.json asks for (<c>framework</c> or <c>frameworks</c>).</summary>
	public static List<(string Name, ReleaseVersion Version)> ReadRequiredFrameworks(string runtimeConfigJson)
	{
		var result = new List<(string, ReleaseVersion)>();
		using var document = JsonDocument.Parse(runtimeConfigJson);
		if (!document.RootElement.TryGetProperty("runtimeOptions", out var options))
		{
			return result;
		}

		var frameworks = new List<JsonElement>();
		if (options.TryGetProperty("framework", out var single)) frameworks.Add(single);
		if (options.TryGetProperty("frameworks", out var many) && many.ValueKind == JsonValueKind.Array) frameworks.AddRange(many.EnumerateArray());

		foreach (var framework in frameworks)
		{
			if (framework.TryGetProperty("name", out var name) && framework.TryGetProperty("version", out var version) &&
				ReleaseVersion.TryParse(version.GetString(), out var parsed) && name.GetString() is { Length: > 0 } frameworkName)
			{
				result.Add((frameworkName, parsed));
			}
		}

		return result;
	}

	/// <summary>The default roll-forward policy: the same major, and a later minor or a patch at least as new.</summary>
	public static bool SatisfiesRollForward(ReleaseVersion installed, ReleaseVersion required) =>
		installed.Major == required.Major &&
		(installed.Minor > required.Minor || (installed.Minor == required.Minor && installed.Patch >= required.Patch)) &&
		(!installed.IsPrerelease || required.IsPrerelease);

	private void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Couldn't remove the update staging folder {Path}", path);
		}
	}
}

public interface IInstalledRuntimeProvider
{
	/// <summary>The shared runtimes the dotnet host running this app can load, or null if it can't be asked.</summary>
	Task<IReadOnlyList<(string Name, ReleaseVersion Version)>?> GetInstalledRuntimesAsync(CancellationToken cancellationToken);
}

public sealed class DotnetInstalledRuntimeProvider : IInstalledRuntimeProvider
{
	public async Task<IReadOnlyList<(string Name, ReleaseVersion Version)>?> GetInstalledRuntimesAsync(CancellationToken cancellationToken)
	{
		var host = ResolveDotnetHost();
		if (host is null)
		{
			return null;
		}

		try
		{
			using var process = Process.Start(new ProcessStartInfo(host, "--list-runtimes")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false
			});
			if (process is null)
			{
				return null;
			}

			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(TimeSpan.FromSeconds(30));
			var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
			await process.WaitForExitAsync(timeout.Token);
			return process.ExitCode == 0 ? ParseListRuntimes(output) : null;
		}
		catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
		{
			return null;
		}
	}

	/// <summary>Lines such as <c>Microsoft.AspNetCore.App 10.0.5 [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]</c>.</summary>
	public static List<(string Name, ReleaseVersion Version)> ParseListRuntimes(string output)
	{
		var result = new List<(string, ReleaseVersion)>();
		foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length >= 2 && ReleaseVersion.TryParse(parts[1], out var version))
			{
				result.Add((parts[0], version));
			}
		}

		return result;
	}

	private static string? ResolveDotnetHost()
	{
		// Run as "dotnet VibeSwarm.Web.dll", the process is the host itself.
		var processPath = Environment.ProcessPath;
		if (processPath is not null && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
		{
			return processPath;
		}

		var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
		if (!string.IsNullOrWhiteSpace(dotnetRoot))
		{
			var candidate = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}

		return null;
	}
}
