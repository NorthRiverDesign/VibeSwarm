using Bunit;
using Bunit.JSInterop;
using VibeSwarm.Client.Components.Jobs;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Tests;

public sealed class CreateJobModalTests
{
[Fact]
public void SubmitWithoutProvider_ShowsValidationMessage()
{
using var context = new BunitContext();
context.JSInterop.SetupVoid("eval", "document.body.classList.add('vs-modal-open')");

var cut = context.Render<CreateJobModal>(parameters => parameters
.Add(component => component.IsVisible, true)
.Add(component => component.JobModel, new Job())
.Add(component => component.Providers, new List<Provider>
{
new()
{
Id = Guid.NewGuid(),
Name = "Copilot",
Type = ProviderType.Copilot,
IsEnabled = true
}
}));

cut.Find("form").Submit();

Assert.Contains("Please select a provider.", cut.Markup);
}

[Fact]
public void SubmitPullRequestWithoutTargetBranch_ShowsValidationMessage()
{
using var context = new BunitContext();
context.JSInterop.SetupVoid("eval", "document.body.classList.add('vs-modal-open')");
var providerId = Guid.NewGuid();

var cut = context.Render<CreateJobModal>(parameters => parameters
.Add(component => component.IsVisible, true)
.Add(component => component.JobModel, new Job
{
GoalPrompt = "Ship a fix",
ProviderId = providerId,
GitChangeDeliveryMode = GitChangeDeliveryMode.PullRequest
})
.Add(component => component.Providers, new List<Provider>
{
new()
{
Id = providerId,
Name = "Copilot",
Type = ProviderType.Copilot,
IsEnabled = true
}
}));

cut.Find("form").Submit();

Assert.Contains("Target branch is required when creating a pull request.", cut.Markup);
}
}
