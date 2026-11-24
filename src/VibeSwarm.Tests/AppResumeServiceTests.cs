using VibeSwarm.Client.Services;

namespace VibeSwarm.Tests;

public sealed class AppResumeServiceTests
{
	[Fact]
	public async Task NotifyResumedAsync_RunsEveryHandlerEvenWhenOneFails()
	{
		var service = new AppResumeService();
		var reloaded = new List<string>();
		service.Resumed += () =>
		{
			reloaded.Add("queue");
			return Task.CompletedTask;
		};
		service.Resumed += () => throw new HttpRequestException("network not back yet");
		service.Resumed += () =>
		{
			reloaded.Add("page");
			return Task.CompletedTask;
		};

		await service.NotifyResumedAsync();

		Assert.Equal(["queue", "page"], reloaded);
	}

	[Fact]
	public async Task NotifyResumedAsync_SkipsHandlersThatUnsubscribed()
	{
		var service = new AppResumeService();
		var calls = 0;
		Task Handler()
		{
			calls++;
			return Task.CompletedTask;
		}

		service.Resumed += Handler;
		service.Resumed -= Handler;
		await service.NotifyResumedAsync();

		Assert.Equal(0, calls);
	}
}
