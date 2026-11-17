namespace VibeSwarm.Web;

/// <summary>
/// Picks the addresses the server listens on from the configured URLs (ASPNETCORE_URLS)
/// and the "Serve over HTTPS" setting. HTTPS addresses are only bound while the setting is on,
/// so browsers reaching the app over the LAN or a VPN never see the self-signed certificate.
/// </summary>
public static class ServerUrlResolver
{
	public const string DefaultHttpUrl = "http://localhost:5000";
	public const string DefaultHttpsUrl = "https://localhost:5001";

	public static ServerUrlResolution Resolve(string? configuredUrls, bool enableHttps)
	{
		var urls = (configuredUrls ?? string.Empty)
			.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.ToList();

		if (urls.Count == 0)
		{
			return new ServerUrlResolution(enableHttps ? [DefaultHttpUrl, DefaultHttpsUrl] : [DefaultHttpUrl], null);
		}

		if (enableHttps)
		{
			return new ServerUrlResolution(urls, urls.Any(IsHttps)
				? null
				: "HTTPS is on in Settings, but ASPNETCORE_URLS has no https:// address, so only HTTP is served.");
		}

		var httpUrls = urls.Where(url => !IsHttps(url)).ToList();
		if (httpUrls.Count == 0)
		{
			// Dropping every address would leave nothing to connect to, so keep HTTPS.
			return new ServerUrlResolution(urls,
				"HTTPS is off in Settings, but ASPNETCORE_URLS lists only https:// addresses, so HTTPS stays on. Add an http:// address to serve HTTP.");
		}

		return new ServerUrlResolution(httpUrls, httpUrls.Count == urls.Count
			? null
			: $"HTTPS is off in Settings; not listening on {string.Join(", ", urls.Where(IsHttps))}.");
	}

	public static bool IsHttps(string url) => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}

public sealed record ServerUrlResolution(IReadOnlyList<string> Urls, string? Message)
{
	public bool ServesHttps => Urls.Any(ServerUrlResolver.IsHttps);
}
