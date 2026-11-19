using System.Text.RegularExpressions;

namespace VibeSwarm.Tests;

public sealed class MainLayoutQueueRefreshTests
{
	[Fact]
	public void MainLayout_RefreshesQueueWhenGlobalJobCompletes()
	{
		var layoutMarkup = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "Shared", "MainLayout.razor"));

		Assert.Matches(
			new Regex(@"private\s+async\s+Task\s+OnGlobalJobCompleted\s*\([^)]*\)\s*\{[\s\S]*?QueuePanelStateService\.RequestRefreshAsync\(\);", RegexOptions.Multiline),
			layoutMarkup);
	}

	[Fact]
	public void MainLayout_RefreshesQueueAndRecordsHistoryWhenGlobalJobStatusChanges()
	{
		var layoutMarkup = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "Shared", "MainLayout.razor"));

		Assert.Matches(
			new Regex(@"private\s+async\s+Task\s+OnGlobalJobStatusChanged\s*\([^)]*\)\s*\{[\s\S]*?QueuePanelStateService\.RequestRefreshAsync\(\);[\s\S]*?RecordJobStatusNotificationAsync\(jobId,\s*status\);", RegexOptions.Multiline),
			layoutMarkup);
		Assert.Contains("NotificationService.AddHistory(", layoutMarkup);
	}

	[Fact]
	public void MainLayout_RefreshesQueueWhenGlobalHubReconnects()
	{
		var layoutMarkup = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "Shared", "MainLayout.razor"));

		Assert.Matches(
			new Regex(@"_globalHubConnection\.Reconnected\s*\+=[\s\S]*?InvokeAsync\(""SubscribeToJobList""\);\s*(//[^\n]*\s*)?await\s+QueuePanelStateService\.RequestRefreshAsync\(\);", RegexOptions.Multiline),
			layoutMarkup);
	}

	[Fact]
	public void MainLayout_RefreshesQueueAndPageWhenAppComesBackToTheForeground()
	{
		var layoutMarkup = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "Shared", "MainLayout.razor"));
		var indexHtml = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "wwwroot", "index.html"));

		Assert.Contains("JSRuntime.InvokeVoidAsync(\"vibeSwarmRegisterAppResume\", _dotNetRef)", layoutMarkup, StringComparison.Ordinal);
		Assert.Matches(
			new Regex(@"\[JSInvokable\]\s*public\s+async\s+Task\s+OnAppResumed\(\)\s*\{[\s\S]*?QueuePanelStateService\.RequestRefreshAsync\(\)[\s\S]*?AppResumeService\.NotifyResumedAsync\(\)", RegexOptions.Multiline),
			layoutMarkup);

		Assert.Matches(
			new Regex(@"window\.vibeSwarmRegisterAppResume = function[\s\S]*?invokeMethodAsync\('OnAppResumed'\)[\s\S]*?document\.addEventListener\('visibilitychange'"),
			indexHtml);
	}

	[Fact]
	public void MainLayout_DoesNotRenderThemeToggle()
	{
		var layoutMarkup = File.ReadAllText(GetRepositoryPath("src", "VibeSwarm.Client", "Shared", "MainLayout.razor"));

		Assert.DoesNotContain("<ThemeToggle", layoutMarkup, StringComparison.Ordinal);
	}

	private static string GetRepositoryPath(params string[] segments)
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);

		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VibeSwarm.sln")))
		{
			directory = directory.Parent;
		}

		Assert.NotNull(directory);
		return Path.Combine([directory.FullName, .. segments]);
	}
}
