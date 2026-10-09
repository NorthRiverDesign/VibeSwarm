using Bunit;
using VibeSwarm.Client.Components.Jobs;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Tests;

public sealed class JobChangeSetsCardTests
{
	[Fact]
	public void ChangeSet_ShowsSavedWorkAndTheFilesItChanged()
	{
		using var context = new BunitContext();
		var saved = new JobChangeSet
		{
			Id = Guid.NewGuid(),
			FollowUpIndex = 0,
			WorkSnapshotCommit = "fedcba9876543210",
			GitDiff = """
				=== Committed changes since abc1234 ===
				diff --git a/src/App.cs b/src/App.cs
				--- a/src/App.cs
				+++ b/src/App.cs
				@@ -1,2 +1,2 @@
				-old
				+new

				=== Uncommitted changes ===
				diff --git a/src/App.cs b/src/App.cs
				--- a/src/App.cs
				+++ b/src/App.cs
				@@ -1 +1,2 @@
				 new
				+more
				"""
		};
		var delivered = new JobChangeSet { Id = Guid.NewGuid(), FollowUpIndex = 1, GitCommitHash = "abc1234567" };

		var cut = context.Render<JobChangeSetsCard>(parameters => parameters
			.Add(component => component.ChangeSets, new List<JobChangeSet> { saved, delivered }));

		var runs = cut.FindAll("details");
		Assert.Equal(2, runs.Count);
		Assert.Contains("Work saved", runs[0].TextContent);
		Assert.DoesNotContain("Work saved", runs[1].TextContent);

		var file = Assert.Single(runs[0].QuerySelectorAll("li"));
		Assert.Contains("src/App.cs", file.TextContent);
		Assert.Contains("+2", file.TextContent);
		Assert.Contains("1", file.QuerySelector(".text-danger")!.TextContent);
		Assert.Empty(runs[1].QuerySelectorAll("li"));
	}
}
