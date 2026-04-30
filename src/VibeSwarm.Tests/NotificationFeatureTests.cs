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
	public void ShowProjectSuccess_UsesProjectNameAsTitle()
	{
		var notificationService = new NotificationService();

		notificationService.ShowProjectSuccess("Demo Project", "Successfully synced with origin.");

		var notification = Assert.Single(notificationService.Notifications);
		Assert.Equal("Demo Project", notification.Title);
		Assert.Equal(NotificationType.Success, notification.Type);
		Assert.Equal("Successfully synced with origin.", notification.Message);
	}

	[Fact]
	public void ShowProjectError_FallsBackToGenericTitle_WhenProjectNameMissing()
	{
		var notificationService = new NotificationService();

		notificationService.ShowProjectError("   ", "Failed to sync with origin.");

		var notification = Assert.Single(notificationService.Notifications);
		Assert.Equal("Error", notification.Title);
		Assert.Equal(NotificationType.Error, notification.Type);
		Assert.Equal("Failed to sync with origin.", notification.Message);
	}

	[Fact]
	public void ShowProjectWarning_UsesProjectNameAsTitle()
	{
		var notificationService = new NotificationService();

		notificationService.ShowProjectWarning("Demo Project", "A job is already running.");

		var notification = Assert.Single(notificationService.Notifications);
		Assert.Equal("Demo Project", notification.Title);
		Assert.Equal(NotificationType.Warning, notification.Type);
		Assert.Equal("A job is already running.", notification.Message);
	}

	[Fact]
	public void ShowProjectInfo_FallsBackToGenericTitle_WhenProjectNameMissing()
	{
		var notificationService = new NotificationService();

		notificationService.ShowProjectInfo(null, "No queued jobs to prioritize.");

		var notification = Assert.Single(notificationService.Notifications);
		Assert.Equal("Info", notification.Title);
		Assert.Equal(NotificationType.Info, notification.Type);
		Assert.Equal("No queued jobs to prioritize.", notification.Message);
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

	[Fact]
	public void AddHistory_AddsPanelEntryWithoutActiveToast()
	{
		var notificationService = new NotificationService();

		notificationService.AddHistory("Job moved to planning.", "Job Planning", NotificationType.Info, "View Job", "/jobs/view/123");

		Assert.Empty(notificationService.Notifications);
		var notification = Assert.Single(notificationService.NotificationHistory);
		Assert.Equal("Job Planning", notification.Title);
		Assert.Equal("View Job", notification.ActionLabel);
		Assert.Equal("/jobs/view/123", notification.ActionUrl);
		Assert.Equal(1, notificationService.UnreadCount);
	}

	[Fact]
	public void AddHistory_DoesNotIncrementUnread_WhenPanelIsOpen()
	{
		var notificationService = new NotificationService();
		notificationService.OpenPanel();

		notificationService.AddHistory("Job stopped responding.", "Job Stalled", NotificationType.Error);

		Assert.Equal(0, notificationService.UnreadCount);
		Assert.Single(notificationService.NotificationHistory);
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
