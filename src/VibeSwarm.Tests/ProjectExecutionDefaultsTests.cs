using VibeSwarm.Client.Models;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Tests;

public sealed class ProjectExecutionDefaultsTests
{
	[Fact]
	public void GetAllowedProviders_ProjectSelectionsExist_ReturnsSelectionOrder()
	{
		var firstProvider = CreateProvider("Claude", ProviderType.Claude);
		var secondProvider = CreateProvider("Copilot", ProviderType.Copilot, isDefault: true);
		var ignoredProvider = CreateProvider("OpenCode", ProviderType.OpenCode);
		var project = new Project
		{
			ProviderSelections =
			[
				new ProjectProvider { ProviderId = firstProvider.Id, Priority = 0, IsEnabled = true },
				new ProjectProvider { ProviderId = secondProvider.Id, Priority = 1, IsEnabled = true }
			]
		};

		var providers = ProjectExecutionDefaults.GetAllowedProviders(project, [ignoredProvider, secondProvider, firstProvider]);

		Assert.Collection(
			providers,
			provider => Assert.Equal(firstProvider.Id, provider.Id),
			provider => Assert.Equal(secondProvider.Id, provider.Id));
	}

	[Fact]
	public void ResolveModelId_ProjectPreferredModelExists_ReturnsProjectModel()
	{
		var providerId = Guid.NewGuid();
		var project = new Project
		{
			ProviderSelections =
			[
				new ProjectProvider
				{
					ProviderId = providerId,
					Priority = 0,
					IsEnabled = true,
					PreferredModelId = "claude-opus-4.6"
				}
			]
		};
		var models = new List<ProviderModel>
		{
			new() { ProviderId = providerId, ModelId = "claude-sonnet-4.6", IsAvailable = true, IsDefault = true },
			new() { ProviderId = providerId, ModelId = "claude-opus-4.6", IsAvailable = true, IsDefault = false }
		};

		var resolvedModelId = ProjectExecutionDefaults.ResolveModelId(project, providerId, models);

		Assert.Equal("claude-opus-4.6", resolvedModelId);
	}

	[Fact]
	public void ResolveModelId_ProjectPreferredModelMissing_FallsBackToProviderDefault()
	{
		var providerId = Guid.NewGuid();
		var project = new Project
		{
			ProviderSelections =
			[
				new ProjectProvider
				{
					ProviderId = providerId,
					Priority = 0,
					IsEnabled = true,
					PreferredModelId = "missing-model"
				}
			]
		};
		var models = new List<ProviderModel>
		{
			new() { ProviderId = providerId, ModelId = "gpt-5.4", IsAvailable = true, IsDefault = true },
			new() { ProviderId = providerId, ModelId = "gpt-5-mini", IsAvailable = true, IsDefault = false }
		};

		var resolvedModelId = ProjectExecutionDefaults.ResolveModelId(project, providerId, models);

		Assert.Equal("gpt-5.4", resolvedModelId);
	}

	private static Provider CreateProvider(string name, ProviderType type, bool isDefault = false)
	{
		return new Provider
		{
			Id = Guid.NewGuid(),
			Name = name,
			Type = type,
			IsEnabled = true,
			IsDefault = isDefault
		};
	}
}
