using Bunit;
using Microsoft.AspNetCore.Components;
using VibeSwarm.Client.Components.Git;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Tests;

public sealed class GitDiffViewerTests
{
	[Fact]
	public void GitDiffViewer_Bunit_ExpandsAndCollapsesAllFiles()
	{
		using var context = new BunitContext();

		var cut = context.Render<GitDiffViewer>(parameters => parameters
			.Add(viewer => viewer.DiffFiles, CreateDiffFiles()));

		var panels = cut.FindAll(".accordion-collapse");
		Assert.Contains("show", panels[0].ClassName);
		Assert.DoesNotContain("show", panels[1].ClassName);

		cut.FindAll("button")
			.Single(button => button.GetAttribute("title") == "Expand all files")
			.Click();

		foreach (var panel in cut.FindAll(".accordion-collapse"))
		{
			Assert.Contains("show", panel.ClassName);
		}

		cut.FindAll("button")
			.Single(button => button.GetAttribute("title") == "Collapse all files")
			.Click();

		foreach (var panel in cut.FindAll(".accordion-collapse"))
		{
			Assert.DoesNotContain("show", panel.ClassName);
		}
	}

	[Fact]
	public void GitDiffViewer_Bunit_TogglesVisibilityAndInvokesCallback()
	{
		using var context = new BunitContext();
		bool? visibleState = null;

		var cut = context.Render<GitDiffViewer>(parameters => parameters
			.Add(viewer => viewer.DiffFiles, CreateDiffFiles())
			.Add(viewer => viewer.IsVisible, true)
			.Add(viewer => viewer.IsVisibleChanged, async (bool isVisible) =>
			{
				visibleState = isVisible;
				await Task.CompletedTask;
			}));

		cut.FindAll("button")
			.Single(button => button.TextContent.Contains("Hide", StringComparison.Ordinal))
			.Click();

		Assert.False(visibleState ?? true);
		Assert.Empty(cut.FindAll(".accordion"));
		Assert.Contains("Show", cut.Markup);
	}

	[Fact]
	public void GitDiffViewer_Bunit_ShowsDivergenceDetailsAndHandlesRecheck()
	{
		using var context = new BunitContext();
		var rechecked = false;

		var cut = context.Render<GitDiffViewer>(parameters => parameters
			.Add(viewer => viewer.DiffFiles, CreateDiffFiles())
			.Add(viewer => viewer.ShowVerificationStatus, true)
			.Add(viewer => viewer.IsDiverged, true)
			.Add(viewer => viewer.MissingFiles, new List<string> { "src/Missing.cs" })
			.Add(viewer => viewer.ExtraFiles, new List<string> { "src/Extra.cs" })
			.Add(viewer => viewer.ModifiedFiles, new List<string> { "src/Changed.cs" })
			.Add(viewer => viewer.OnRecheck, async () =>
			{
				rechecked = true;
				await Task.CompletedTask;
			}));

		Assert.Contains("Working copy has diverged from job changes", cut.Markup);
		Assert.Contains("src/Missing.cs", cut.Markup);
		Assert.Contains("src/Extra.cs", cut.Markup);
		Assert.Contains("src/Changed.cs", cut.Markup);

		cut.Find("button.btn-warning").Click();

		Assert.True(rechecked);
	}

	[Fact]
	public void GitDiffViewer_Bunit_HidesCommandNoise_FromRenderedDiff()
	{
		using var context = new BunitContext();

		var cut = context.Render<GitDiffViewer>(parameters => parameters
			.Add(viewer => viewer.DiffFiles, new List<DiffFile>
			{
				new()
				{
					FileName = "src/Noisy.cs",
					Additions = 1,
					Deletions = 1,
					DiffContent = """
						diff --git a/src/Noisy.cs b/src/Noisy.cs
						index 1111111..2222222 100644
						--- a/src/Noisy.cs
						+++ b/src/Noisy.cs
						@@ -1 +1 @@
						-old value
						+new value
						$ git status --short
						 M src/Noisy.cs
						"""
				}
			}));

		Assert.Contains("old value", cut.Markup);
		Assert.Contains("new value", cut.Markup);
		Assert.DoesNotContain("$ git status --short", cut.Markup, StringComparison.Ordinal);
		Assert.DoesNotContain("M src/Noisy.cs", cut.Markup, StringComparison.Ordinal);
	}

	[Fact]
	public void GitDiffViewer_Bunit_RendersCustomTitleAndFooterTemplate()
	{
		using var context = new BunitContext();

		var cut = context.Render<GitDiffViewer>(parameters => parameters
			.Add(viewer => viewer.DiffFiles, CreateDiffFiles())
			.Add(viewer => viewer.Title, "Conflicted Files")
			.Add(viewer => viewer.Icon, "exclamation-triangle")
			.Add<RenderFragment<DiffFile>>(viewer => viewer.FileFooterTemplate, file => builder =>
			{
				builder.AddMarkupContent(0, $"<div class=\"conflict-editor\">Editor for {file.FileName}</div>");
			}));

		Assert.Contains("Conflicted Files", cut.Markup);
		Assert.Contains("Editor for src/First.cs", cut.Markup);
	}

	private static List<DiffFile> CreateDiffFiles()
	{
		return new List<DiffFile>
		{
			new()
			{
				FileName = "src/First.cs",
				Additions = 2,
				Deletions = 1,
				DiffContent = """
					diff --git a/src/First.cs b/src/First.cs
					index 1111111..2222222 100644
					--- a/src/First.cs
					+++ b/src/First.cs
					@@ -1,2 +1,3 @@
					-old line
					 context line
					+new line
					+another line
					"""
			},
			new()
			{
				FileName = "src/Second.cs",
				Additions = 1,
				Deletions = 0,
				IsNew = true,
				DiffContent = """
					diff --git a/src/Second.cs b/src/Second.cs
					new file mode 100644
					index 0000000..3333333
					--- /dev/null
					+++ b/src/Second.cs
					@@ -0,0 +1 @@
					+created line
					"""
			}
		};
	}
}
