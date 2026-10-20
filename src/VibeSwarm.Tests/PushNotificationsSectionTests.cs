using Bunit;
using Microsoft.Extensions.DependencyInjection;
using VibeSwarm.Client.Components.Settings;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class PushNotificationsSectionTests
{
	private const string Endpoint = "https://web.push.apple.com/device";

	[Fact]
	public void OverPlainHttp_SaysHttpsIsNeededAndLeavesTheSwitchOff()
	{
		using var context = CreateContext(new FakePushNotificationService(), new PushNotificationsSection.BrowserPushState
		{
			Https = false,
			Supported = true
		});

		var cut = context.Render<PushNotificationsSection>();

		cut.WaitForAssertion(() => Assert.Contains("Needs the https:// address", cut.Markup));
		Assert.True(cut.Find("#pushNotifications").HasAttribute("disabled"));
		Assert.Empty(cut.FindAll("#pushOnCompleted"));
	}

	[Fact]
	public void OnAnIPhoneInSafari_AsksForTheHomeScreenApp()
	{
		using var context = CreateContext(new FakePushNotificationService(), new PushNotificationsSection.BrowserPushState
		{
			Https = true,
			Supported = false,
			NeedsHomeScreen = true
		});

		var cut = context.Render<PushNotificationsSection>();

		cut.WaitForAssertion(() => Assert.Contains("Add VibeSwarm to your Home Screen", cut.Markup));
	}

	[Fact]
	public void TurningOn_SignsTheBrowserUpAndSavesTheDevice()
	{
		var service = new FakePushNotificationService();
		using var context = CreateContext(service, Ready(subscription: null), module => module
			.Setup<PushNotificationsSection.SubscribeResult>("subscribe", "server-key")
			.SetResult(new PushNotificationsSection.SubscribeResult { Subscription = BrowserSubscription() }));

		var cut = context.Render<PushNotificationsSection>();
		cut.WaitForAssertion(() => Assert.False(cut.Find("#pushNotifications").HasAttribute("disabled")));
		cut.Find("#pushNotifications").Change(true);

		cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pushOnCompleted")));
		var saved = Assert.Single(service.Saved);
		Assert.Equal(Endpoint, saved.Endpoint);
		Assert.Equal("iPhone", saved.DeviceName);
		Assert.Null(saved.NotifyOnCompleted);
		Assert.Contains("On for this device", cut.Markup);
	}

	[Fact]
	public void ASignedUpDevice_SavesEachEventSwitch()
	{
		var service = new FakePushNotificationService
		{
			Device = new PushDeviceSettings { Endpoint = Endpoint }
		};
		using var context = CreateContext(service, Ready(BrowserSubscription()));

		var cut = context.Render<PushNotificationsSection>();
		cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#pushOnFailed")));
		cut.Find("#pushOnFailed").Change(false);

		cut.WaitForAssertion(() => Assert.Single(service.Saved));
		var saved = service.Saved[0];
		Assert.False(saved.NotifyOnFailed);
		Assert.True(saved.NotifyOnCompleted);
		Assert.True(saved.NotifyOnInputRequired);
		Assert.Equal("p256dh-key", saved.P256dh);
	}

	private static BunitContext CreateContext(FakePushNotificationService service, PushNotificationsSection.BrowserPushState state,
		Action<BunitJSModuleInterop>? setUpModule = null)
	{
		var context = new BunitContext();
		context.Services.AddSingleton<IPushNotificationService>(service);
		context.Services.AddSingleton<NotificationService>();
		var module = context.JSInterop.SetupModule(PushNotificationsSection.ModulePath);
		module.Setup<PushNotificationsSection.BrowserPushState>("getState").SetResult(state);
		setUpModule?.Invoke(module);
		return context;
	}

	private static PushNotificationsSection.BrowserPushState Ready(PushNotificationsSection.BrowserPushSubscription? subscription) => new()
	{
		Https = true,
		Supported = true,
		HasWorker = true,
		Permission = subscription == null ? "default" : "granted",
		Subscription = subscription,
		DeviceName = "iPhone"
	};

	private static PushNotificationsSection.BrowserPushSubscription BrowserSubscription() => new()
	{
		Endpoint = Endpoint,
		P256dh = "p256dh-key",
		Auth = "auth-secret"
	};

	private sealed class FakePushNotificationService : IPushNotificationService
	{
		public PushDeviceSettings? Device { get; set; }
		public List<PushSubscriptionRequest> Saved { get; } = [];

		public Task<string> GetPublicKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult("server-key");

		public Task<PushDeviceSettings?> GetDeviceAsync(string endpoint, CancellationToken cancellationToken = default)
			=> Task.FromResult(Device?.Endpoint == endpoint ? Device : null);

		public Task<PushDeviceSettings> SaveDeviceAsync(PushSubscriptionRequest request, CancellationToken cancellationToken = default)
		{
			Saved.Add(request);
			Device = new PushDeviceSettings
			{
				Endpoint = request.Endpoint,
				DeviceName = request.DeviceName,
				NotifyOnCompleted = request.NotifyOnCompleted ?? Device?.NotifyOnCompleted ?? true,
				NotifyOnFailed = request.NotifyOnFailed ?? Device?.NotifyOnFailed ?? true,
				NotifyOnInputRequired = request.NotifyOnInputRequired ?? Device?.NotifyOnInputRequired ?? true
			};
			return Task.FromResult(Device);
		}

		public Task DeleteDeviceAsync(string endpoint, CancellationToken cancellationToken = default)
		{
			Device = null;
			return Task.CompletedTask;
		}

		public Task<PushTestResult> SendTestAsync(string endpoint, CancellationToken cancellationToken = default)
			=> Task.FromResult(new PushTestResult { Success = true });
	}
}
