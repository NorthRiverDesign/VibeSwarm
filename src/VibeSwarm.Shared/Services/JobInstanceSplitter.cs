using System.Text.RegularExpressions;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Divides a goal written as a list between the instances of a job, the same way every time, so
/// each instance knows its own part without having to agree with the others on a split.
/// </summary>
public static partial class JobInstanceSplitter
{
	[GeneratedRegex(@"^ {0,3}(?:[-*+]|\d{1,3}[.)])\s+(?<text>\S.*)$")]
	private static partial Regex TopLevelItem();

	/// <summary>
	/// The goal's top-level list items, in order. Lines indented under an item stay with it;
	/// a blank or unindented line ends it.
	/// </summary>
	public static IReadOnlyList<string> GetListItems(string? goal)
	{
		var items = new List<string>();
		if (string.IsNullOrWhiteSpace(goal))
		{
			return items;
		}

		string? current = null;
		foreach (var rawLine in goal.Replace("\r\n", "\n").Split('\n'))
		{
			var line = rawLine.Replace("\t", "    ").TrimEnd();
			var match = TopLevelItem().Match(line);
			if (match.Success)
			{
				if (current != null)
				{
					items.Add(current);
				}

				current = match.Groups["text"].Value.Trim();
				continue;
			}

			if (current != null && line.Length > 0 && char.IsWhiteSpace(line[0]))
			{
				current += " " + line.Trim();
				continue;
			}

			if (current != null)
			{
				items.Add(current);
				current = null;
			}
		}

		if (current != null)
		{
			items.Add(current);
		}

		return items;
	}

	/// <summary>
	/// The list items instance <paramref name="instanceNumber"/> (from 1) of
	/// <paramref name="instanceCount"/> owns: a run of neighbouring items, the first instances
	/// taking one extra when they don't divide evenly. Null when the goal has fewer items than
	/// instances, so there is no list to split.
	/// </summary>
	public static IReadOnlyList<string>? GetPart(string? goal, int instanceNumber, int instanceCount)
	{
		if (instanceCount < 2 || instanceNumber < 1 || instanceNumber > instanceCount)
		{
			return null;
		}

		var items = GetListItems(goal);
		if (items.Count < instanceCount)
		{
			return null;
		}

		var baseSize = items.Count / instanceCount;
		var remainder = items.Count % instanceCount;
		var index = instanceNumber - 1;
		var start = (index * baseSize) + Math.Min(index, remainder);
		var size = baseSize + (index < remainder ? 1 : 0);
		return items.Skip(start).Take(size).ToList();
	}
}
