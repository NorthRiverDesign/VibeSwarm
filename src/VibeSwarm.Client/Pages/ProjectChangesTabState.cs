using VibeSwarm.Shared.VersionControl.Models;

namespace VibeSwarm.Client.Pages;

public static class ProjectChangesTabState
{
	public static HashSet<int> PreserveExpandedItems(
		IReadOnlyList<DiffFile> previousFiles,
		IReadOnlyCollection<int> previousExpandedItems,
		IReadOnlyList<DiffFile> nextFiles)
	{
		if (nextFiles.Count == 0)
		{
			return [];
		}

		if (previousFiles.Count == 0)
		{
			return [0];
		}

		if (previousExpandedItems.Count == 0)
		{
			return [];
		}

		var expandedKeys = previousExpandedItems
			.Where(index => index >= 0 && index < previousFiles.Count)
			.Select(index => CreateFileKey(previousFiles[index]))
			.ToHashSet(StringComparer.Ordinal);

		var remappedItems = new HashSet<int>();
		for (var index = 0; index < nextFiles.Count; index++)
		{
			if (expandedKeys.Contains(CreateFileKey(nextFiles[index])))
			{
				remappedItems.Add(index);
			}
		}

		return remappedItems;
	}

	private static string CreateFileKey(DiffFile file)
		=> $"{file.FileName}|{file.IsNew}|{file.IsDeleted}";
}
