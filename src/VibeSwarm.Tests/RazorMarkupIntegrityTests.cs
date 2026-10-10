using System.Text.RegularExpressions;

namespace VibeSwarm.Tests;

public sealed class RazorMarkupIntegrityTests
{
	// An attribute value followed by a lone quote, such as <summary class="a" " : "b")">. Razor
	// compiles the quote into an attribute named '"'. Chromium accepts that, but Safari throws
	// InvalidCharacterError and Blazor stops rendering the whole app.
	private static readonly Regex StrayQuoteAfterAttribute = new("=\"[^\"\\n]*\"\\s+\"", RegexOptions.Compiled);

	[Fact]
	public void RazorMarkup_HasNoStrayQuoteAfterAnAttribute()
	{
		var clientDirectory = GetRepositoryPath("src", "VibeSwarm.Client");
		var offenders = Directory.EnumerateFiles(clientDirectory, "*.razor", SearchOption.AllDirectories)
			.SelectMany(path => File.ReadLines(path)
				.Select((line, index) => (line, number: index + 1))
				.Where(entry => StrayQuoteAfterAttribute.IsMatch(entry.line))
				.Select(entry => $"{Path.GetRelativePath(clientDirectory, path)}:{entry.number}: {entry.line.Trim()}"))
			.ToList();

		Assert.True(offenders.Count == 0, "Stray quote after an attribute:\n" + string.Join("\n", offenders));
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
