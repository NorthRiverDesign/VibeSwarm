using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace VibeSwarm.Tests;

/// <summary>Serves canned responses by URL, recording what was asked for.</summary>
internal sealed class UrlStubHttpHandler : HttpMessageHandler, IHttpClientFactory
{
	private readonly Dictionary<string, Func<HttpResponseMessage>> _responses = new(StringComparer.Ordinal);

	public List<string> Requests { get; } = [];

	public void Add(string url, HttpStatusCode status, string body) =>
		_responses[url] = () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

	public void Add(string url, byte[] body) =>
		_responses[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

	public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://api.github.com/") };

	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		var url = request.RequestUri!.ToString();
		Requests.Add(url);
		return Task.FromResult(_responses.TryGetValue(url, out var response)
			? response()
			: new HttpResponseMessage(HttpStatusCode.NotFound));
	}
}

internal static class ReleasePackageBuilder
{
	public const string RuntimeConfig = """
		{
		  "runtimeOptions": {
		    "tfm": "net10.0",
		    "frameworks": [
		      { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
		      { "name": "Microsoft.AspNetCore.App", "version": "10.0.0" }
		    ]
		  }
		}
		""";

	/// <summary>
	/// A .tar.gz shaped like release.yml's: a top folder holding the web app. The dll is this build's
	/// VibeSwarm.Web.dll, so its stamped version is the build's (0.0.0-dev unless stamped).
	/// </summary>
	public static byte[] Build(Action<TarWriter>? extra = null, string? runtimeConfig = null)
	{
		var webDll = Path.Combine(AppContext.BaseDirectory, "VibeSwarm.Web.dll");
		using var buffer = new MemoryStream();
		using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
		using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
		{
			tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "vibeswarm/"));
			tar.WriteEntry(webDll, "vibeswarm/VibeSwarm.Web.dll");
			AddText(tar, "vibeswarm/VibeSwarm.Web.runtimeconfig.json", runtimeConfig ?? RuntimeConfig);
			AddText(tar, "vibeswarm/Deploy/launcher.sh", "#!/usr/bin/env bash\n");
			extra?.Invoke(tar);
		}

		return buffer.ToArray();
	}

	public static void AddText(TarWriter tar, string name, string text)
	{
		tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
	}

	public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
