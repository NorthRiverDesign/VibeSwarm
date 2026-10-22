using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Client.Components.Chat;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Tests;

public sealed class ToolOutputContentTests
{
	[Fact]
	public void Parse_TakesImageDataUrlLinesOutOfTheText()
	{
		var content = ToolOutputContent.Parse("Took the screenshot\ndata:image/png;base64,iVBORw0KGgo=\ndata:image/jpeg;base64,/9j/4AAQ+Sk=");

		Assert.Equal("Took the screenshot", content.Text);
		Assert.Equal(["data:image/png;base64,iVBORw0KGgo=", "data:image/jpeg;base64,/9j/4AAQ+Sk="], content.Images);
	}

	[Theory]
	[InlineData("Plain output, no images")]
	[InlineData("data:image/png;base64, see the file above")]
	[InlineData("data:text/html;base64,PHNjcmlwdD4=")]
	[InlineData("data:image/svg+xml;base64,PHN2Zz4=")]
	[InlineData("data:image/png;base64,")]
	public void Parse_LeavesOtherOutputAsText(string output)
	{
		var content = ToolOutputContent.Parse(output);

		Assert.Equal(output, content.Text);
		Assert.Empty(content.Images);
	}

	[Theory]
	[InlineData("image/png", "iVBORw0KGgo=", "data:image/png;base64,iVBORw0KGgo=")]
	[InlineData("IMAGE/JPEG", "/9j/4AAQ", "data:image/jpeg;base64,/9j/4AAQ")]
	[InlineData("image/svg+xml", "PHN2Zz4=", null)]
	[InlineData("image/png", "", null)]
	[InlineData(null, "iVBORw0KGgo=", null)]
	public void ToImageDataUrl_AcceptsOnlyRasterImagesWithData(string? mediaType, string data, string? expected)
	{
		Assert.Equal(expected, ToolOutputContent.ToImageDataUrl(mediaType, data));
	}

	[Fact]
	public async Task RenderedToolResult_ShowsTheImageInsteadOfItsBase64()
	{
		var services = new ServiceCollection();
		services.AddLogging();

		await using var renderer = new HtmlRenderer(services.BuildServiceProvider(), NullLoggerFactory.Instance);

		var html = await renderer.Dispatcher.InvokeAsync(async () =>
		{
			var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
			{
				[nameof(ChatMessage.Role)] = MessageRole.ToolResult,
				[nameof(ChatMessage.ToolName)] = "mcp__playwright__browser_take_screenshot",
				[nameof(ChatMessage.ToolOutput)] = "Took the screenshot\ndata:image/png;base64,iVBORw0KGgo=",
				[nameof(ChatMessage.Timestamp)] = DateTime.UtcNow
			});

			var output = await renderer.RenderComponentAsync<ChatMessage>(parameters);
			return output.ToHtmlString();
		});

		Assert.Contains("src=\"data:image/png;base64,iVBORw0KGgo=\"", html);
		Assert.Contains("Took the screenshot</pre>", html);
	}
}
