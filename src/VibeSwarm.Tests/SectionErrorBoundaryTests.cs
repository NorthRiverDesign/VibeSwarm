using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Components.Common;

namespace VibeSwarm.Tests;

public sealed class SectionErrorBoundaryTests : IDisposable
{
	private readonly BunitContext _context = new();

	[Fact]
	public void SectionErrorBoundary_ShowsFallback_WhenChildThrows()
	{
		var cut = _context.Render<SectionErrorBoundary>(parameters => parameters
			.Add(component => component.SectionTitle, "live output")
			.AddChildContent<AlwaysThrowingChild>());

		Assert.Contains("Couldn't load live output.", cut.Markup);
		Assert.Contains("Show error details", cut.Markup);
		Assert.Contains("InvalidOperationException: Section exploded.", cut.Markup);
	}

	[Fact]
	public void SectionErrorBoundary_RetryRendersChild_WhenIssueClears()
	{
		var state = new FlakyChildState();
		_context.Services.AddSingleton(state);

		var cut = _context.Render<SectionErrorBoundary>(parameters => parameters
			.Add(component => component.SectionTitle, "job list")
			.AddChildContent<FlakyChild>());

		Assert.Contains("Couldn't load job list.", cut.Markup);

		state.ShouldThrow = false;

		cut.Find("button").Click();

		Assert.Contains("Recovered section content", cut.Markup);
		Assert.DoesNotContain("Couldn't load job list.", cut.Markup);
	}

	public void Dispose()
	{
		_context.Dispose();
	}

	private sealed class FlakyChildState
	{
		public bool ShouldThrow { get; set; } = true;
	}

	private sealed class AlwaysThrowingChild : ComponentBase
	{
		protected override void BuildRenderTree(RenderTreeBuilder builder)
		{
			throw new InvalidOperationException("Section exploded.");
		}
	}

	private sealed class FlakyChild : ComponentBase
	{
		[Inject]
		private FlakyChildState State { get; set; } = default!;

		protected override void BuildRenderTree(RenderTreeBuilder builder)
		{
			if (State.ShouldThrow)
			{
				throw new InvalidOperationException("Temporary failure.");
			}

			builder.AddContent(0, "Recovered section content");
		}
	}
}
