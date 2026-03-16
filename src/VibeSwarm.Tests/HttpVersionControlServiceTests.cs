using System.Net;
using System.Text;
using System.Text.Json;
using VibeSwarm.Client.Services;

namespace VibeSwarm.Tests;

public sealed class HttpVersionControlServiceTests
{
	[Fact]
	public async Task GetWorkingDirectoryDiffAsync_ReturnsPlainTextDiff_WhenEndpointReturnsTextPlain()
	{
		const string diff = """
diff --git a/src/App.cs b/src/App.cs
index 1111111..2222222 100644
--- a/src/App.cs
+++ b/src/App.cs
@@ -1 +1 @@
-old
+new
""";

		var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(diff, Encoding.UTF8, "text/plain")
		});

		var result = await service.GetWorkingDirectoryDiffAsync("/repo");

		Assert.Equal(diff, result);
	}

	[Fact]
	public async Task GetWorkingDirectoryDiffAsync_ReturnsJsonStringValue_WhenEndpointReturnsApplicationJson()
	{
		const string diff = """
diff --git a/src/App.cs b/src/App.cs
index 1111111..2222222 100644
--- a/src/App.cs
+++ b/src/App.cs
@@ -1 +1 @@
-old
+new
""";

		var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(JsonSerializer.Serialize(diff), Encoding.UTF8, "application/json")
		});

		var result = await service.GetWorkingDirectoryDiffAsync("/repo");

		Assert.Equal(diff, result);
	}

	[Fact]
	public async Task GetCommitRangeDiffAsync_ReturnsPlainTextDiff_WhenEndpointReturnsTextPlain()
	{
		const string diff = """
diff --git a/src/Feature.cs b/src/Feature.cs
index 3333333..4444444 100644
--- a/src/Feature.cs
+++ b/src/Feature.cs
@@ -1 +1,2 @@
 public class Feature { }
+public class NextFeature { }
""";

		var service = CreateService(request =>
		{
			Assert.Equal("/api/git/diff-range?path=%2Frepo&from=abc123&to=def456", request.RequestUri?.PathAndQuery);
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(diff, Encoding.UTF8, "text/plain")
			};
		});

		var result = await service.GetCommitRangeDiffAsync("/repo", "abc123", "def456");

		Assert.Equal(diff, result);
	}

	[Fact]
	public async Task GetRemoteUrlAsync_ReturnsJsonStringValue_WhenEndpointReturnsApplicationJson()
	{
		const string remoteUrl = "git@github.com:octocat/Hello-World.git";

		var service = CreateService(request =>
		{
			Assert.Equal("/api/git/remote-url?path=%2Frepo&remote=origin", request.RequestUri?.PathAndQuery);
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(JsonSerializer.Serialize(remoteUrl), Encoding.UTF8, "application/json")
			};
		});

		var result = await service.GetRemoteUrlAsync("/repo");

		Assert.Equal(remoteUrl, result);
	}

	private static HttpVersionControlService CreateService(Func<HttpRequestMessage, HttpResponseMessage> handler)
	{
		var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) => Task.FromResult(handler(request))))
		{
			BaseAddress = new Uri("http://localhost")
		};

		return new HttpVersionControlService(httpClient);
	}

	private sealed class StubHttpMessageHandler : HttpMessageHandler
	{
		private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

		public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
		{
			_handler = handler;
		}

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
			=> _handler(request, cancellationToken);
	}
}
