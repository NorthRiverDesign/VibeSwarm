using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Client.Components.Chat;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Tests;

public sealed class ToolCallFormatterTests
{
	[Fact]
	public void Format_ShellCall_ReadsByDescriptionWithTheCommandOnItsOwn()
	{
		var view = ToolCallFormatter.Format("Bash", """{"command":"ls -la src","description":"List source files"}""");

		Assert.Equal("terminal", view.Icon);
		Assert.Equal("List source files", view.Title);
		Assert.Equal("ls -la src", view.Preview);
		Assert.Equal("ls -la src", view.Command);
		Assert.Empty(view.Fields);
		Assert.Null(view.RawInput);
	}

	[Fact]
	public void Format_ShellCallWithoutDescription_KeepsTheToolNameAndPreviewsTheFirstLine()
	{
		var view = ToolCallFormatter.Format("bash", """{"command":"python3 - <<'EOF'\nprint(1)\nEOF","timeout":120000,"run_in_background":false}""");

		Assert.Equal("bash", view.Title);
		Assert.Equal("python3 - <<'EOF' …", view.Preview);
		Assert.Equal("python3 - <<'EOF'\nprint(1)\nEOF", view.Command);
		// A false flag is the default and left out; the rest are labelled values.
		var field = Assert.Single(view.Fields);
		Assert.Equal("Timeout", field.Label);
		Assert.Equal("120000", field.Value);
		Assert.False(field.IsBlock);
	}

	[Fact]
	public void Format_ShellCallWithPlainTextInput_TreatsTheTextAsTheCommand()
	{
		var view = ToolCallFormatter.Format("bash", "git status --short");

		Assert.Equal("git status --short", view.Command);
		Assert.Null(view.RawInput);
	}

	[Fact]
	public void Format_ReadCall_NamesTheFileAndItsLineRange()
	{
		var view = ToolCallFormatter.Format("Read", """{"file_path":"/repo/src/Pages/Dashboard.vue","offset":448,"limit":15}""");

		Assert.Equal("Read Dashboard.vue", view.Title);
		Assert.Equal("/repo/src/Pages/Dashboard.vue", view.Preview);
		Assert.Null(view.Command);
		var lines = Assert.Single(view.Fields);
		Assert.Equal("Lines", lines.Label);
		Assert.Equal("448–462", lines.Value);
	}

	[Fact]
	public void Format_EditCall_ShowsWhatIsReplacedAsBlocks()
	{
		var view = ToolCallFormatter.Format("Edit", """{"replace_all":false,"file_path":"/repo/Program.cs","old_string":"var a = 1;\nvar b = 2;","new_string":"var a = 3;"}""");

		Assert.Equal("Edit Program.cs", view.Title);
		Assert.Collection(view.Fields,
			find =>
			{
				Assert.Equal("Find", find.Label);
				Assert.True(find.IsBlock);
			},
			replace =>
			{
				Assert.Equal("Replace with", replace.Label);
				Assert.Equal("var a = 3;", replace.Value);
				// Code reads as code, even a one-liner.
				Assert.True(replace.IsBlock);
			});
	}

	[Fact]
	public void Format_CopilotEditorView_ReadsAsARead()
	{
		var view = ToolCallFormatter.Format("str_replace_editor", """{"command":"view","path":"/repo/README.md","view_range":[1,40]}""");

		Assert.Equal("Read README.md", view.Title);
		Assert.Equal("1–40", Assert.Single(view.Fields).Value);
	}

	[Fact]
	public void Format_SearchCall_QuotesThePattern()
	{
		var view = ToolCallFormatter.Format("Grep", """{"pattern":"ToolInput","path":"src","output_mode":"content"}""");

		Assert.Equal("Search “ToolInput”", view.Title);
		Assert.Equal("src", view.Preview);
		Assert.Equal("Output mode", Assert.Single(view.Fields).Label);
	}

	[Fact]
	public void Format_UnknownTool_PreviewsItsFirstShortArgumentAndPrettyPrintsTheRest()
	{
		var view = ToolCallFormatter.Format("mcp__playwright__browser_navigate", """{"url":"http://localhost:5099/jobs","options":{"wait":true}}""");

		Assert.Equal("tools", view.Icon);
		Assert.Equal("http://localhost:5099/jobs", view.Preview);
		var options = Assert.Single(view.Fields);
		Assert.Equal("Options", options.Label);
		Assert.True(options.IsBlock);
		Assert.Contains("\"wait\": true", options.Value);
	}

	[Fact]
	public void Format_NonShellPlainTextInput_IsKeptAsIs()
	{
		var view = ToolCallFormatter.Format("rg", "rg ToolUse src");

		Assert.Equal("rg", view.Title);
		Assert.Equal("rg ToolUse src", view.RawInput);
		Assert.Null(view.Command);
	}

	[Fact]
	public void Format_MissingInput_ShowsJustTheToolName()
	{
		var view = ToolCallFormatter.Format(null, null);

		Assert.Equal("unknown_tool", view.Title);
		Assert.Null(view.Preview);
		Assert.Empty(view.Fields);
	}

	[Fact]
	public async Task RenderedChatMessage_ShowsAShellCallAsATerminalLineWithItsOutput()
	{
		var services = new ServiceCollection();
		services.AddLogging();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(ChatMessage.Role)] = MessageRole.ToolUse,
				[nameof(ChatMessage.ToolName)] = "Bash",
				[nameof(ChatMessage.ToolInput)] = """{"command":"dotnet test","description":"Run the tests"}""",
				[nameof(ChatMessage.ToolOutput)] = "Passed!",
				[nameof(ChatMessage.Timestamp)] = DateTime.UtcNow
			});

			var output = await renderer.RenderComponentAsync<ChatMessage>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("Run the tests", html);
		Assert.Contains("$ </span>dotnet test", html);
		Assert.Contains("Passed!", html);
		Assert.DoesNotContain("&quot;command&quot;", html);
	}
}
