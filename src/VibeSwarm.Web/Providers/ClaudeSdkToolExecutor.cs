using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VibeSwarm.Shared.Providers;

/// <summary>
/// Runs the Anthropic-defined bash and text editor tools for <see cref="ClaudeSdkProvider"/>, so
/// SDK mode can do a job's work the way the CLI does. Every path is confined to the job's
/// checkout. Commands run as the service user in that checkout, the same trust the CLI provider
/// gets with permissions bypassed.
/// </summary>
internal sealed class ClaudeSdkToolExecutor
{
	public const string BashToolName = "bash";
	public const string EditorToolName = "str_replace_based_edit_tool";
	internal const int MaxOutputChars = 30_000;
	private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

	private readonly string _root;
	private readonly IReadOnlyDictionary<string, string>? _environment;

	public ClaudeSdkToolExecutor(string root, IReadOnlyDictionary<string, string>? environment = null)
	{
		_root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
		_environment = environment;
	}

	/// <summary>False during planning: the editor may only view.</summary>
	public bool AllowEdits { get; init; } = true;

	public async Task<(string Output, bool IsError)> RunAsync(string toolName, JsonElement input, CancellationToken cancellationToken)
	{
		try
		{
			return toolName switch
			{
				BashToolName => await RunBashAsync(input, cancellationToken),
				EditorToolName => RunEditor(input),
				_ => ($"Unknown tool '{toolName}'.", true)
			};
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			return ($"The tool failed: {ex.Message}", true);
		}
	}

	private async Task<(string Output, bool IsError)> RunBashAsync(JsonElement input, CancellationToken cancellationToken)
	{
		if (input.TryGetProperty("restart", out var restart) && restart.ValueKind == JsonValueKind.True)
		{
			return ("Shell restarted.", false);
		}

		var command = GetString(input, "command");
		if (string.IsNullOrWhiteSpace(command))
		{
			return ("No command given.", true);
		}

		var startInfo = new ProcessStartInfo("/bin/bash")
		{
			WorkingDirectory = _root,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		startInfo.ArgumentList.Add("-c");
		startInfo.ArgumentList.Add(command);
		if (_environment != null)
		{
			foreach (var (key, value) in _environment)
			{
				startInfo.Environment[key] = value;
			}
		}

		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("bash did not start.");
		process.StandardInput.Close();
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(CommandTimeout);
		var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
		var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
		try
		{
			await process.WaitForExitAsync(timeout.Token);
		}
		catch (OperationCanceledException)
		{
			try { process.Kill(entireProcessTree: true); } catch { }
			cancellationToken.ThrowIfCancellationRequested();
			return ($"The command was stopped after {CommandTimeout.TotalMinutes:0} minutes.", true);
		}

		var output = new StringBuilder(await stdout);
		var errors = await stderr;
		if (errors.Length > 0)
		{
			output.Append(output.Length > 0 && output[^1] != '\n' ? "\n" : string.Empty).Append(errors);
		}
		if (process.ExitCode != 0)
		{
			output.Append($"\n[exit code {process.ExitCode}]");
		}

		return (Truncate(output.ToString()), process.ExitCode != 0);
	}

	private (string Output, bool IsError) RunEditor(JsonElement input)
	{
		var command = GetString(input, "command");
		var path = ResolvePath(GetString(input, "path"));
		if (path == null)
		{
			return ("The path must be inside the project checkout.", true);
		}

		if (command != "view" && !AllowEdits)
		{
			return ("Editing files is not allowed in this stage. Only view is available.", true);
		}

		switch (command)
		{
			case "view":
				return View(path, input);
			case "create":
				var text = GetString(input, "file_text") ?? string.Empty;
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				File.WriteAllText(path, text);
				return ($"Wrote {RelativePath(path)}.", false);
			case "str_replace":
				return Replace(path, GetString(input, "old_str"), GetString(input, "new_str") ?? string.Empty);
			case "insert":
				return Insert(path, input);
			default:
				return ($"Unknown editor command '{command}'.", true);
		}
	}

	private (string Output, bool IsError) View(string path, JsonElement input)
	{
		if (Directory.Exists(path))
		{
			var entries = Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1 })
				.Select(RelativePath)
				.Where(entry => !entry.Split('/').Any(part => part.StartsWith('.')))
				.Order(StringComparer.Ordinal);
			return (Truncate(string.Join('\n', entries)), false);
		}

		if (!File.Exists(path))
		{
			return ($"{RelativePath(path)} does not exist.", true);
		}

		var lines = File.ReadAllLines(path);
		var first = 1;
		var last = lines.Length;
		if (input.TryGetProperty("view_range", out var range) && range.ValueKind == JsonValueKind.Array && range.GetArrayLength() == 2)
		{
			first = Math.Max(1, range[0].GetInt32());
			var end = range[1].GetInt32();
			last = end < 0 ? lines.Length : Math.Min(lines.Length, end);
		}

		var numbered = new StringBuilder();
		for (var i = first; i <= last; i++)
		{
			numbered.Append(i).Append('\t').Append(lines[i - 1]).Append('\n');
		}

		return (Truncate(numbered.ToString()), false);
	}

	private (string Output, bool IsError) Replace(string path, string? oldText, string newText)
	{
		if (!File.Exists(path))
		{
			return ($"{RelativePath(path)} does not exist.", true);
		}
		if (string.IsNullOrEmpty(oldText))
		{
			return ("old_str is required.", true);
		}

		var content = File.ReadAllText(path);
		var first = content.IndexOf(oldText, StringComparison.Ordinal);
		if (first < 0)
		{
			return ($"old_str was not found in {RelativePath(path)}.", true);
		}
		if (content.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal) >= 0)
		{
			return ($"old_str appears more than once in {RelativePath(path)}. Include more context to make it unique.", true);
		}

		File.WriteAllText(path, string.Concat(content.AsSpan(0, first), newText, content.AsSpan(first + oldText.Length)));
		return ($"Edited {RelativePath(path)}.", false);
	}

	private (string Output, bool IsError) Insert(string path, JsonElement input)
	{
		if (!File.Exists(path))
		{
			return ($"{RelativePath(path)} does not exist.", true);
		}

		var text = GetString(input, "insert_text") ?? GetString(input, "new_str") ?? string.Empty;
		var lines = File.ReadAllLines(path).ToList();
		var after = input.TryGetProperty("insert_line", out var line) ? line.GetInt32() : -1;
		if (after < 0 || after > lines.Count)
		{
			return ($"insert_line must be between 0 and {lines.Count}.", true);
		}

		lines.InsertRange(after, text.Split('\n'));
		File.WriteAllText(path, string.Join('\n', lines) + "\n");
		return ($"Inserted text into {RelativePath(path)} after line {after}.", false);
	}

	/// <summary>
	/// Resolves a model-supplied path and returns null if it leaves the checkout, including
	/// through a symbolic link.
	/// </summary>
	internal string? ResolvePath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}

		var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_root, path));
		if (!IsInsideRoot(full))
		{
			return null;
		}

		var linked = File.Exists(full) || Directory.Exists(full)
			? new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName
			: null;
		return linked == null || IsInsideRoot(Path.GetFullPath(linked)) ? full : null;
	}

	private bool IsInsideRoot(string fullPath) =>
		fullPath == _root || fullPath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

	private string RelativePath(string fullPath) => Path.GetRelativePath(_root, fullPath).Replace('\\', '/');

	private static string? GetString(JsonElement input, string name) =>
		input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	/// <summary>Keeps the start and end of long output, where errors and summaries usually are.</summary>
	internal static string Truncate(string text)
	{
		if (text.Length <= MaxOutputChars)
		{
			return text;
		}

		var half = MaxOutputChars / 2;
		return $"{text[..half]}\n[... {text.Length - MaxOutputChars} characters cut ...]\n{text[^half..]}";
	}
}
