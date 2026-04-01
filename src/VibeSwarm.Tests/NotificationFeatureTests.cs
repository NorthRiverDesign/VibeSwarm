using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using VibeSwarm.Client.Components.Common;
using VibeSwarm.Client.Services;
using VibeSwarm.Client.Shared;

namespace VibeSwarm.Tests;

public sealed class NotificationFeatureTests
{
	[Fact]
	public void ShowJobCompleted_AddsViewJobActionMetadata()
	{
		var notificationService = new NotificationService();
		var jobId = Guid.NewGuid();

		notificationService.ShowJobCompleted(jobId, success: true, projectName: "Demo Project");

		var notification = Assert.Single(notificationService.Notifications);
		Assert.Equal("Job Completed", notification.Title);
		Assert.Equal("View Job", notification.ActionLabel);
		Assert.Equal($"/jobs/view/{jobId}", notification.ActionUrl);
		Assert.True(notification.HasAction);
	}

	[Fact]
	public void ToastContainer_ViewJobButton_NavigatesToJob()
	{
		using var context = new BunitContext();
		var notificationService = new NotificationService();
		var navigationManager = new TestNavigationManager();
		var jobId = Guid.NewGuid();

		context.Services.AddSingleton(notificationService);
		context.Services.AddSingleton<NavigationManager>(navigationManager);
		context.Services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

		notificationService.ShowJobCompleted(jobId, success: true, projectName: "Demo Project");

		var cut = context.Render<ToastContainer>();
		var viewJobButton = cut.FindAll("button")
			.Single(button => button.TextContent.Contains("View Job", StringComparison.Ordinal));

		viewJobButton.Click();

		Assert.Equal($"http://localhost/jobs/view/{jobId}", navigationManager.Uri);
		Assert.Empty(notificationService.Notifications);
	}

	[Fact]
	public void NotificationsPanelOverlay_ClickingJobNotification_NavigatesToJob()
	{
		using var context = new BunitContext();
		var notificationService = new NotificationService();
		var navigationManager = new TestNavigationManager();
		var jobId = Guid.NewGuid();

		context.Services.AddSingleton(notificationService);
		context.Services.AddSingleton<NavigationManager>(navigationManager);

		notificationService.ShowJobCompleted(jobId, success: true, projectName: "Demo Project");
		notificationService.OpenPanel();

		var cut = context.Render<NotificationsPanelOverlay>();
		var historyItem = cut.Find(".notification-history-item");

		historyItem.Click();

		Assert.Equal($"http://localhost/jobs/view/{jobId}", navigationManager.Uri);
		Assert.False(notificationService.IsPanelOpen);
	}

	private sealed class NoOpJsRuntime : IJSRuntime
	{
		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);

		public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
			=> ValueTask.FromResult(default(TValue)!);
	}

	private sealed class TestNavigationManager : NavigationManager
	{
		public TestNavigationManager()
		{
			Initialize("http://localhost/", "http://localhost/");
		}

		protected override void NavigateToCore(string uri, bool forceLoad)
		{
			Uri = ToAbsoluteUri(uri).ToString();
		}
	}
}
