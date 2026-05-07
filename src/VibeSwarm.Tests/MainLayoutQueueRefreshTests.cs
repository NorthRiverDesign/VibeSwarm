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
