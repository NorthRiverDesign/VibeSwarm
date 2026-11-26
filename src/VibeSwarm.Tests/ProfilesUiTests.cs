using Bunit;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Components.Profiles;
using VibeSwarm.Client.Components.Projects;
using VibeSwarm.Client.Models;
using VibeSwarm.Client.Pages;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class ProfilesUiTests
{
	private static readonly Provider Claude = new() { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude, ConnectionMode = ProviderConnectionMode.CLI, IsEnabled = true };
	private static readonly Provider OpenCode = new() { Id = Guid.NewGuid(), Name = "OpenCode", Type = ProviderType.OpenCode, ConnectionMode = ProviderConnectionMode.CLI, IsEnabled = true };

	[Fact]
	public void ProfilesPage_ListsEachProfileWithWhatItRunsOn()
	{
		using var context = new BunitContext();
		context.JSInterop.Mode = JSRuntimeMode.Loose;
		context.Services.AddSingleton<IProfileService>(new FakeProfileService(
		[
			new Profile { Id = Guid.NewGuid(), Name = "Deep thinker", ProviderId = Claude.Id, Provider = Claude, ModelId = "opus", ReasoningEffort = "high" },
		]));
		context.Services.AddSingleton<IProviderService>(new FakeProviderService());
		context.Services.AddSingleton<NotificationService>();

		var cut = context.Render<Profiles>();

		cut.WaitForAssertion(() => Assert.Contains("Deep thinker", cut.Markup));
		Assert.Contains("Claude · opus · high", cut.Markup);
		Assert.Contains("aria-label=\"Add a profile\"", cut.Markup);
	}

	[Fact]
	public void LeadSection_PickingAProfileLinksItAndHidesTheManualFields()
	{
		using var context = new BunitContext();
		context.Services.AddSingleton<IProviderService>(new FakeProviderService());
		var thinker = new Profile { Id = Guid.NewGuid(), Name = "Deep thinker", ProviderId = Claude.Id, Provider = Claude, ModelId = "opus", ReasoningEffort = "high" };
		var other = new Profile { Id = Guid.NewGuid(), Name = "Open coder", ProviderId = OpenCode.Id, Provider = OpenCode };
		var project = new ProjectModalFormModel();

		var cut = context.Render<ProjectLeadSection>(parameters => parameters
			.Add(section => section.Project, project)
			.Add(section => section.AvailableProviders, [Claude])
			.Add(section => section.Profiles, [thinker, other]));

		// OpenCode can't lead, so its profile isn't offered.
		Assert.Empty(cut.FindAll($"#modal-leadProfile option[value='{other.Id}']"));
		Assert.NotEmpty(cut.FindAll("#modal-leadProvider"));

		cut.Find("#modal-leadProfile").Change(thinker.Id.ToString());

		Assert.Equal(thinker.Id, project.LeadProfileId);
		Assert.Equal((Claude.Id, "opus", "high"), (project.LeadProviderId!.Value, project.LeadModelId, project.LeadReasoningEffort));
		Assert.Empty(cut.FindAll("#modal-leadProvider"));
		Assert.NotEmpty(cut.FindAll("#modal-leadDirection"));

		cut.Find("#modal-leadProfile").Change(string.Empty);

		Assert.Null(project.LeadProfileId);
		Assert.Equal(Claude.Id, project.LeadProviderId);
		Assert.NotEmpty(cut.FindAll("#modal-leadProvider"));
	}

	[Fact]
	public void ProfilePicker_RendersNothingWithoutProfiles()
	{
		using var context = new BunitContext();
		var cut = context.Render<ProfilePicker>(parameters => parameters.Add(picker => picker.Id, "picker"));
		Assert.Empty(cut.FindAll("select"));
	}

	private sealed class FakeProfileService(IReadOnlyList<Profile> profiles) : IProfileService
	{
		public Task<IEnumerable<Profile>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<Profile>>(profiles);
		public Task<Profile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(profiles.FirstOrDefault(profile => profile.Id == id));
		public Task<Profile> CreateAsync(Profile profile, CancellationToken cancellationToken = default) => Task.FromResult(profile);
		public Task<Profile> UpdateAsync(Profile profile, CancellationToken cancellationToken = default) => Task.FromResult(profile);
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
	}

	private sealed class FakeProviderService : FakeProviderServiceBase
	{
		public override Task<IEnumerable<Provider>> GetAllAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IEnumerable<Provider>>([Claude, OpenCode]);
	}
}
