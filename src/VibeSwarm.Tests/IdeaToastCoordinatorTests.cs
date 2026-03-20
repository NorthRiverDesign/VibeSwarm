using VibeSwarm.Client.Services;

namespace VibeSwarm.Tests;

public sealed class IdeaToastCoordinatorTests
{
	[Fact]
	public async Task ShouldShowIdeaCreatedAsync_ReturnsTrue_WhenIdeaDoesNotStart()
	{
		var coordinator = new IdeaToastCoordinator(
			ideaUpdateDelay: TimeSpan.FromMilliseconds(20),
			retentionWindow: TimeSpan.FromSeconds(1));

		var shouldShow = await coordinator.ShouldShowIdeaCreatedAsync("idea-1");

		Assert.True(shouldShow);
	}

	[Fact]
	public async Task ShouldShowIdeaCreatedAsync_ReturnsFalse_WhenIdeaStartsImmediatelyAfterCreate()
	{
		var coordinator = new IdeaToastCoordinator(
			ideaUpdateDelay: TimeSpan.FromMilliseconds(30),
			retentionWindow: TimeSpan.FromSeconds(1));

		var shouldShowTask = coordinator.ShouldShowIdeaCreatedAsync("idea-1");
		await Task.Delay(5);
		coordinator.RegisterIdeaStarted("idea-1");

		Assert.False(await shouldShowTask);
	}

	[Fact]
	public async Task ShouldShowIdeaCreatedAsync_ReturnsFalse_WhenIdeaStartedJustBeforeCreateArrives()
	{
		var coordinator = new IdeaToastCoordinator(
			ideaUpdateDelay: TimeSpan.FromMilliseconds(30),
			retentionWindow: TimeSpan.FromSeconds(1));

		coordinator.RegisterIdeaStarted("idea-1");

		Assert.False(await coordinator.ShouldShowIdeaCreatedAsync("idea-1"));
	}

	[Fact]
	public async Task ShouldShowIdeaUpdatedAsync_ReturnsTrue_WhenIdeaDoesNotStart()
	{
		var coordinator = new IdeaToastCoordinator(
			ideaUpdateDelay: TimeSpan.FromMilliseconds(20),
			retentionWindow: TimeSpan.FromSeconds(1));

		var shouldShow = await coordinator.ShouldShowIdeaUpdatedAsync("idea-1");

		Assert.True(shouldShow);
	}

	[Fact]
	public async Task ShouldShowIdeaUpdatedAsync_ReturnsFalse_WhenIdeaStartsImmediatelyAfterUpdate()
	{
		var coordinator = new IdeaToastCoordinator(
			ideaUpdateDelay: TimeSpan.FromMilliseconds(30),
			retentionWindow: TimeSpan.FromSeconds(1));

		var shouldShowTask = coordinator.ShouldShowIdeaUpdatedAsync("idea-1");
		await Task.Delay(5);
		coordinator.RegisterIdeaStarted("idea-1");

		Assert.False(await shouldShowTask);
	}

	[Fact]
	public async Task ShouldShowIdeaUpdatedAsync_ReturnsFalse_WhenIdeaStartedJustBeforeUpdateArrives()
	{
		var coordinator = new IdeaToastCoordinator(
			ideaUpdateDelay: TimeSpan.FromMilliseconds(30),
			retentionWindow: TimeSpan.FromSeconds(1));

		coordinator.RegisterIdeaStarted("idea-1");

		Assert.False(await coordinator.ShouldShowIdeaUpdatedAsync("idea-1"));
	}
}
