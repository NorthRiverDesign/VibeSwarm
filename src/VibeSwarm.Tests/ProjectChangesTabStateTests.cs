using VibeSwarm.Client.Pages;
using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Tests;

public sealed class ProjectChangesTabStateTests
{
	[Fact]
	public void PreserveExpandedItems_AutoExpandsFirstFile_OnInitialLoad()
	{
		var nextFiles = new List<DiffFile>
		{
			new() { FileName = "src/One.cs" },
			new() { FileName = "src/Two.cs" }
		};

		var expandedItems = ProjectChangesTabState.PreserveExpandedItems([], [], nextFiles);

		Assert.Equal([0], expandedItems.OrderBy(index => index));
	}

	[Fact]
	public void PreserveExpandedItems_RemapsExpandedFiles_ByFileName()
	{
		var previousFiles = new List<DiffFile>
		{
			new() { FileName = "src/One.cs" },
			new() { FileName = "src/Two.cs" },
			new() { FileName = "src/Three.cs" }
		};
		var nextFiles = new List<DiffFile>
		{
			new() { FileName = "src/Zero.cs" },
			new() { FileName = "src/Two.cs" },
			new() { FileName = "src/Three.cs" }
		};

		var expandedItems = ProjectChangesTabState.PreserveExpandedItems(previousFiles, [1, 2], nextFiles);

		Assert.Equal([1, 2], expandedItems.OrderBy(index => index));
	}

	[Fact]
	public void PreserveExpandedItems_KeepsFilesCollapsed_AfterReload_WhenUserCollapsedAll()
	{
		var previousFiles = new List<DiffFile>
		{
			new() { FileName = "src/One.cs" }
		};
		var nextFiles = new List<DiffFile>
		{
			new() { FileName = "src/One.cs" },
			new() { FileName = "src/Two.cs" }
		};

		var expandedItems = ProjectChangesTabState.PreserveExpandedItems(previousFiles, [], nextFiles);

		Assert.Empty(expandedItems);
	}
}
