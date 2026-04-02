using System.Net;
using System.Text;
using System.Text.Json;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.VersionControl.Models;

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

	[Fact]
	public async Task PreviewMergeBranchAsync_PostsPreviewRequestToExpectedEndpoint()
	{
		var service = CreateService(async request =>
		{
			Assert.Equal(HttpMethod.Post, request.Method);
			Assert.Equal("/api/git/preview-merge-branch", request.RequestUri?.AbsolutePath);

			var payload = await request.Content!.ReadAsStringAsync();
			Assert.Contains("\"path\":\"/repo\"", payload);
			Assert.Contains("\"sourceBranch\":\"feature/test\"", payload);
			Assert.Contains("\"targetBranch\":\"main\"", payload);
			Assert.Contains("\"remote\":\"origin\"", payload);

			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(
					JsonSerializer.Serialize(GitOperationResult.Succeeded(output: "Preview ok")),
					Encoding.UTF8,
					"application/json")
			};
		});

		var result = await service.PreviewMergeBranchAsync("/repo", "feature/test", "main");

		Assert.True(result.Success);
		Assert.Equal("Preview ok", result.Output);
	}

	[Fact]
	public async Task MergeBranchAsync_SendsPushAfterMergeFlag()
	{
		var service = CreateService(async request =>
		{
			Assert.Equal(HttpMethod.Post, request.Method);
			Assert.Equal("/api/git/merge-branch", request.RequestUri?.AbsolutePath);

			var payload = await request.Content!.ReadAsStringAsync();
			Assert.Contains("\"path\":\"/repo\"", payload);
			Assert.Contains("\"sourceBranch\":\"feature/test\"", payload);
			Assert.Contains("\"targetBranch\":\"main\"", payload);
			Assert.Contains("\"pushAfterMerge\":false", payload);

			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(
					JsonSerializer.Serialize(GitOperationResult.Succeeded(output: "Merged locally")),
					Encoding.UTF8,
					"application/json")
			};
		});

		var result = await service.MergeBranchAsync("/repo", "feature/test", "main", pushAfterMerge: false);

		Assert.True(result.Success);
		Assert.Equal("Merged locally", result.Output);
	}

	[Fact]
	public async Task MergeBranchAsync_SendsConflictResolutions_WhenProvided()
	{
		var service = CreateService(async request =>
		{
			Assert.Equal(HttpMethod.Post, request.Method);
			Assert.Equal("/api/git/merge-branch", request.RequestUri?.AbsolutePath);

			var payload = await request.Content!.ReadAsStringAsync();
			Assert.Contains("\"fileName\":\"README.md\"", payload);
			Assert.Contains("\"resolvedContent\":\"resolved content\"", payload);

			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(
					JsonSerializer.Serialize(GitOperationResult.Succeeded(output: "Resolved merge")),
					Encoding.UTF8,
					"application/json")
			};
		});

		var result = await service.MergeBranchAsync(
			"/repo",
			"feature/test",
			"main",
			conflictResolutions:
			[
				new MergeConflictResolution
				{
					FileName = "README.md",
					ResolvedContent = "resolved content"
				}
			]);

		Assert.True(result.Success);
		Assert.Equal("Resolved merge", result.Output);
	}

	private static HttpVersionControlService CreateService(Func<HttpRequestMessage, HttpResponseMessage> handler)
		=> CreateService(request => Task.FromResult(handler(request)));

	private static HttpVersionControlService CreateService(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
	{
		var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) => handler(request)))
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
