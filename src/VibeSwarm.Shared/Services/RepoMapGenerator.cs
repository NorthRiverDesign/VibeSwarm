using System.Text;

namespace VibeSwarm.Shared.Services;

/// <summary>
/// Generates a compact repo map (file tree with one-line summaries) for a project directory.
/// Used to give CLI agents a head start on understanding the project structure.
/// </summary>
public static class RepoMapGenerator
{
	/// <summary>
	/// Maximum character length for the generated repo map.
	/// </summary>
	private const int MaxRepoMapLength = 3000;

	/// <summary>
	/// Directories to always skip during enumeration.
	/// </summary>
	private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
	{
		"node_modules", "bin", "obj", ".git", "dist", "build", "vendor",
		"__pycache__", ".vs", ".idea", ".vscode", "packages", "TestResults",
		"coverage", ".next", ".nuget", "target", "out", ".cache"
	};

	/// <summary>
	/// Recognized source file extensions.
	/// </summary>
	private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".java", ".go", ".rs",
		".rb", ".php", ".swift", ".kt", ".scala", ".vue", ".svelte",
		".razor", ".cshtml", ".fs", ".fsx", ".c", ".cpp", ".h", ".hpp",
		".css", ".scss", ".less", ".html", ".xml", ".json", ".yaml", ".yml",
		".toml", ".md", ".sql", ".sh", ".ps1", ".bat", ".cmd",
		".csproj", ".sln", ".fsproj", ".vbproj"
	};

	/// <summary>
	/// Generates a compact repo map for the given working directory.
	/// </summary>
	/// <param name="workingDirectory">The root directory to scan.</param>
	/// <returns>A compact tree-format string showing files with optional summaries, or null if no source files found.</returns>
	public static string? GenerateRepoMap(string workingDirectory)
	{
		if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
			return null;

		var gitignorePatterns = LoadGitignorePatterns(workingDirectory);
		var entries = new List<FileEntry>();

		CollectFiles(workingDirectory, workingDirectory, gitignorePatterns, entries);

		if (entries.Count == 0)
			return null;

		// Sort entries by relative path for consistent output
		entries.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));

		return BuildTreeOutput(entries);
	}

	private static void CollectFiles(string rootDir, string currentDir, List<string> gitignorePatterns, List<FileEntry> entries)
	{
		try
		{
			// Process files in current directory
			foreach (var file in Directory.GetFiles(currentDir))
			{
				var ext = Path.GetExtension(file);
				if (!SourceExtensions.Contains(ext))
					continue;

				var relativePath = GetRelativePath(rootDir, file);

				if (IsGitignored(relativePath, gitignorePatterns))
					continue;

				var summary = ExtractSummary(file, ext);
				entries.Add(new FileEntry(relativePath, summary));
			}

			// Process subdirectories
			foreach (var dir in Directory.GetDirectories(currentDir))
			{
				var dirName = Path.GetFileName(dir);

				if (SkipDirectories.Contains(dirName) || dirName.StartsWith('.'))
					continue;

				var relativeDirPath = GetRelativePath(rootDir, dir);
				if (IsGitignored(relativeDirPath + "/", gitignorePatterns))
					continue;

				CollectFiles(rootDir, dir, gitignorePatterns, entries);
			}
		}
		catch (UnauthorizedAccessException)
		{
			// Skip directories we can't access
		}
		catch (IOException)
		{
			// Skip directories with I/O errors
		}
	}

	private static string GetRelativePath(string rootDir, string fullPath)
	{
		return Path.GetRelativePath(rootDir, fullPath).Replace('\\', '/');
	}

	/// <summary>
	/// Extracts a one-line summary from a source file based on its type.
	/// </summary>
	private static string? ExtractSummary(string filePath, string extension)
	{
		try
		{
			// Only read the first few lines to extract a summary
			using var reader = new StreamReader(filePath);
			var linesRead = 0;
			const int maxLines = 30;

			while (reader.ReadLine() is { } line && linesRead < maxLines)
			{
				linesRead++;
				var trimmed = line.Trim();

				switch (extension.ToLowerInvariant())
				{
					case ".cs":
					case ".fs":
					case ".fsx":
						// Look for XML doc comment summary
						if (trimmed.StartsWith("/// <summary>"))
						{
							var nextLine = reader.ReadLine()?.Trim();
							if (nextLine != null && nextLine.StartsWith("///"))
							{
								var summary = nextLine.TrimStart('/').Trim();
								if (!string.IsNullOrWhiteSpace(summary) && summary != "</summary>")
									return summary;
							}
						}
						break;

					case ".py":
						// Look for module-level docstring
						if (linesRead <= 5 && (trimmed.StartsWith("\"\"\"") || trimmed.StartsWith("'''")))
						{
							var docString = trimmed[3..];
							if (docString.EndsWith("\"\"\"") || docString.EndsWith("'''"))
								return docString[..^3].Trim();
							if (!string.IsNullOrWhiteSpace(docString))
								return docString.Trim();
						}
						break;

					case ".js":
					case ".jsx":
					case ".ts":
					case ".tsx":
					case ".vue":
					case ".svelte":
						// Look for leading // or /** comment
						if (linesRead <= 3 && trimmed.StartsWith("//"))
						{
							var comment = trimmed.TrimStart('/').Trim();
							if (!string.IsNullOrWhiteSpace(comment))
								return comment;
						}
						if (linesRead <= 3 && trimmed.StartsWith("/**"))
						{
							var comment = trimmed[3..].TrimEnd('*', '/').Trim();
							if (!string.IsNullOrWhiteSpace(comment))
								return comment;
						}
						break;
				}
			}
		}
		catch
		{
			// Ignore file read errors
		}

		return null;
	}

	/// <summary>
	/// Loads .gitignore patterns from the root directory.
	/// </summary>
	private static List<string> LoadGitignorePatterns(string rootDir)
	{
		var patterns = new List<string>();
		var gitignorePath = Path.Combine(rootDir, ".gitignore");

		if (!File.Exists(gitignorePath))
			return patterns;

		try
		{
			foreach (var line in File.ReadAllLines(gitignorePath))
			{
				var trimmed = line.Trim();
				if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
					continue;
				patterns.Add(trimmed);
			}
		}
		catch
		{
			// If we can't read .gitignore, proceed without it
		}

		return patterns;
	}

	/// <summary>
	/// Simple gitignore pattern matching. Handles basic directory and file patterns.
	/// </summary>
	private static bool IsGitignored(string relativePath, List<string> patterns)
	{
		foreach (var pattern in patterns)
		{
			var p = pattern.TrimEnd('/');

			// Simple directory match: if pattern is just a name, match any segment
			if (!p.Contains('/') && !p.Contains('*'))
			{
				var segments = relativePath.Split('/');
				if (segments.Any(s => s.Equals(p, StringComparison.OrdinalIgnoreCase)))
					return true;
			}

			// Pattern with wildcard extension (e.g., *.log)
			if (p.StartsWith("*."))
			{
				var ext = p[1..]; // e.g., ".log"
				if (relativePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
					return true;
			}

			// Direct path match
			if (relativePath.StartsWith(p, StringComparison.OrdinalIgnoreCase))
				return true;
		}

		return false;
	}

	/// <summary>
	/// Builds the tree output from collected file entries, respecting the max length.
	/// </summary>
	private static string BuildTreeOutput(List<FileEntry> entries)
	{
		var sb = new StringBuilder();
		var currentDirParts = Array.Empty<string>();

		foreach (var entry in entries)
		{
			var parts = entry.RelativePath.Split('/');
			var dirParts = parts[..^1];
			var fileName = parts[^1];

			// Determine shared prefix with current directory context
			var commonDepth = 0;
			for (var i = 0; i < Math.Min(currentDirParts.Length, dirParts.Length); i++)
			{
				if (string.Equals(currentDirParts[i], dirParts[i], StringComparison.OrdinalIgnoreCase))
					commonDepth++;
				else
					break;
			}

			// Output new directory levels
			for (var i = commonDepth; i < dirParts.Length; i++)
			{
				var indent = new string(' ', i * 2);
				var dirLine = $"{indent}{dirParts[i]}/";

				if (sb.Length + dirLine.Length + 2 > MaxRepoMapLength)
					return sb.ToString().TrimEnd();

				sb.AppendLine(dirLine);
			}

			currentDirParts = dirParts;

			// Output file entry
			var fileIndent = new string(' ', dirParts.Length * 2);
			var fileLine = entry.Summary != null
				? $"{fileIndent}{fileName} - {entry.Summary}"
				: $"{fileIndent}{fileName}";

			if (sb.Length + fileLine.Length + 2 > MaxRepoMapLength)
				return sb.ToString().TrimEnd();

			sb.AppendLine(fileLine);
		}

		return sb.ToString().TrimEnd();
	}

	private record FileEntry(string RelativePath, string? Summary);
}
