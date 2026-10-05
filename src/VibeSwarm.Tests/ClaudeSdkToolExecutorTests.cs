using System.Text.Json;
using Anthropic.Models.Messages;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Tests;

public sealed class ClaudeSdkToolExecutorTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("vibeswarm-sdk-tools-").FullName;
	private readonly string _outside = Directory.CreateTempSubdirectory("vibeswarm-sdk-outside-").FullName;

	[Fact]
	public async Task Editor_CreatesViewsReplacesAndInserts()
	{
		var tools = new ClaudeSdkToolExecutor(_root);

		Assert.False((await EditAsync(tools, new { command = "create", path = "src/app.txt", file_text = "one\ntwo\n" })).IsError);
		Assert.False((await EditAsync(tools, new { command = "str_replace", path = "src/app.txt", old_str = "two", new_str = "TWO" })).IsError);
		Assert.False((await EditAsync(tools, new { command = "insert", path = "src/app.txt", insert_line = 0, insert_text = "zero" })).IsError);

		var (view, isError) = await EditAsync(tools, new { command = "view", path = "src/app.txt" });
		Assert.False(isError);
		Assert.Equal("1\tzero\n2\tone\n3\tTWO\n", view);
	}

	[Fact]
	public async Task Editor_RefusesAmbiguousReplacements()
	{
		File.WriteAllText(Path.Combine(_root, "dup.txt"), "x\nx\n");
		var tools = new ClaudeSdkToolExecutor(_root);

		var (output, isError) = await EditAsync(tools, new { command = "str_replace", path = "dup.txt", old_str = "x", new_str = "y" });

		Assert.True(isError);
		Assert.Contains("more than once", output);
		Assert.Equal("x\nx\n", File.ReadAllText(Path.Combine(_root, "dup.txt")));
	}

	[Fact]
	public void Paths_StayInsideTheCheckout()
	{
		var secret = Path.Combine(_outside, "secret.txt");
		File.WriteAllText(secret, "secret");
		File.CreateSymbolicLink(Path.Combine(_root, "link.txt"), secret);
		var tools = new ClaudeSdkToolExecutor(_root);

		Assert.Null(tools.ResolvePath("../outside.txt"));
		Assert.Null(tools.ResolvePath(secret));
		Assert.Null(tools.ResolvePath("link.txt"));
		Assert.Equal(Path.Combine(_root, "src", "new.cs"), tools.ResolvePath("src/new.cs"));
	}

	[Fact]
	public async Task Planning_CanViewButNotEdit()
	{
		File.WriteAllText(Path.Combine(_root, "app.txt"), "keep\n");
		var tools = new ClaudeSdkToolExecutor(_root) { AllowEdits = false };

		Assert.False((await EditAsync(tools, new { command = "view", path = "app.txt" })).IsError);
		Assert.True((await EditAsync(tools, new { command = "create", path = "app.txt", file_text = "changed" })).IsError);
		Assert.Equal("keep\n", File.ReadAllText(Path.Combine(_root, "app.txt")));
	}

	[Fact]
	public async Task Bash_RunsInTheCheckoutAndReportsFailures()
	{
		var tools = new ClaudeSdkToolExecutor(_root, new Dictionary<string, string> { ["JOB_VALUE"] = "42" });

		var (output, isError) = await tools.RunAsync("bash", Json(new { command = "pwd && echo $JOB_VALUE" }), CancellationToken.None);
		Assert.False(isError);
		Assert.Equal($"{_root}\n42\n", output);

		(output, isError) = await tools.RunAsync("bash", Json(new { command = "echo broken >&2; exit 3" }), CancellationToken.None);
		Assert.True(isError);
		Assert.Contains("broken", output);
		Assert.Contains("[exit code 3]", output);
	}

	[Theory]
	[InlineData("end_turn")]
	[InlineData("tool_use")]
	[InlineData("max_tokens")]
	public void StopReason_IsReadAsTheWireValue(string stopReason)
	{
		var json = "{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-5-5\"," +
			"\"content\":[{\"type\":\"text\",\"text\":\"done\"}],\"stop_reason\":\"" + stopReason + "\",\"stop_sequence\":null," +
			"\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}";
		var message = JsonSerializer.Deserialize<Message>(json)!;

		Assert.Equal(stopReason, ClaudeSdkProvider.StopReasonOf(message));
	}

	[Fact]
	public async Task Provider_WorksThroughToolsUntilTheModelFinishes()
	{
		using var listener = new System.Net.HttpListener();
		var port = Random.Shared.Next(20000, 40000);
		listener.Prefixes.Add($"http://127.0.0.1:{port}/");
		listener.Start();
		var requests = new List<string>();
		string[] replies =
		[
			"{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-5-5\",\"content\":[" +
				"{\"type\":\"text\",\"text\":\"Creating it.\"}," +
				"{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"str_replace_based_edit_tool\",\"input\":{\"command\":\"create\",\"path\":\"hello.txt\",\"file_text\":\"hi\\n\"}}]," +
				"\"stop_reason\":\"tool_use\",\"stop_sequence\":null,\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}",
			"{\"id\":\"msg_2\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-5-5\",\"content\":[" +
				"{\"type\":\"text\",\"text\":\"Done.\"}],\"stop_reason\":\"end_turn\",\"stop_sequence\":null,\"usage\":{\"input_tokens\":20,\"output_tokens\":3}}"
		];
		var server = Task.Run(async () =>
		{
			foreach (var reply in replies)
			{
				var context = await listener.GetContextAsync();
				using (var reader = new StreamReader(context.Request.InputStream))
				{
					requests.Add(await reader.ReadToEndAsync());
				}
				var body = System.Text.Encoding.UTF8.GetBytes(reply);
				context.Response.ContentType = "application/json";
				await context.Response.OutputStream.WriteAsync(body);
				context.Response.Close();
			}
		});

		var provider = new ClaudeSdkProvider(new Provider
		{
			Id = Guid.NewGuid(),
			Name = "Claude SDK",
			Type = ProviderType.Claude,
			ConnectionMode = ProviderConnectionMode.SDK,
			ApiKey = "sk-ant-test",
			ApiEndpoint = $"http://127.0.0.1:{port}"
		});
		provider.ApplyOptions(new ExecutionOptions { AppendSystemPrompt = "JOB RULES" });

		var result = await provider.ExecuteWithSessionAsync("Create hello.txt", workingDirectory: _root);
		await server;

		Assert.True(result.Success, result.ErrorMessage);
		Assert.Equal("Done.", result.Output);
		Assert.Equal("hi\n", File.ReadAllText(Path.Combine(_root, "hello.txt")));
		Assert.Equal(30, result.InputTokens);
		Assert.Contains("\"bash_20250124\"", requests[0]);
		Assert.Contains("\"text_editor_20250728\"", requests[0]);
		Assert.Contains("JOB RULES", requests[0]);
		Assert.Contains("\"tool_use_id\":\"toolu_1\"", requests[1]);
	}

	private static Task<(string Output, bool IsError)> EditAsync(ClaudeSdkToolExecutor tools, object input) =>
		tools.RunAsync(ClaudeSdkToolExecutor.EditorToolName, Json(input), CancellationToken.None);

	private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

	public void Dispose()
	{
		Directory.Delete(_root, recursive: true);
		Directory.Delete(_outside, recursive: true);
	}
}
