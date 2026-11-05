using Bunit;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Components.Ideas;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class LeadSuggestionsTests
{
	[Fact]
	public void ShowsNothing_WhenTheProjectHasNoLeadAndNoSuggestions()
	{
		using var context = CreateContext(new FakeLeadService());

		var cut = context.Render<LeadSuggestions>(parameters => parameters.Add(p => p.ProjectId, Guid.NewGuid()));

		Assert.Empty(cut.Markup.Trim());
	}

	[Fact]
	public void ApprovingASuggestion_MovesItToTheQueue_AndTellsThePage()
	{
		var idea = new Idea { Id = Guid.NewGuid(), Description = "Add tests for the parser", ExpandedDescription = "Why: no tests.\nGoal: cover it.", NeedsApproval = true };
		var lead = new FakeLeadService(new LeadStatus { IsConfigured = true, Suggestions = [idea] });
		var approvedRaised = false;
		using var context = CreateContext(lead);

		var cut = context.Render<LeadSuggestions>(parameters => parameters
			.Add(p => p.ProjectId, Guid.NewGuid())
			.Add(p => p.OnIdeaApproved, () => approvedRaised = true));

		Assert.Contains("Suggested by Lead", cut.Markup);
		Assert.Contains("no tests.", cut.Find(".list-group-item").TextContent);
		cut.Find(".list-group-item").Click();
		cut.FindAll("button").Single(b => b.TextContent.Contains("Approve")).Click();

		Assert.Equal([idea.Id], lead.Approved);
		Assert.True(approvedRaised);
		Assert.DoesNotContain("Add tests for the parser", cut.Markup);
	}

	[Fact]
	public void ReviewNow_StartsALeadReview()
	{
		var lead = new FakeLeadService(new LeadStatus { IsConfigured = true });
		using var context = CreateContext(lead);

		var cut = context.Render<LeadSuggestions>(parameters => parameters.Add(p => p.ProjectId, Guid.NewGuid()));
		cut.FindAll("button").Single(b => b.TextContent.Contains("Review now")).Click();

		Assert.Equal(1, lead.ReviewsStarted);
	}

	private static BunitContext CreateContext(ILeadService lead)
	{
		var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;
		context.Services.AddSingleton(lead);
		context.Services.AddSingleton<NotificationService>();
		return context;
	}
}
