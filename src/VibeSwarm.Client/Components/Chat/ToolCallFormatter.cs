using System.Text;
using System.Text.Json;

namespace VibeSwarm.Client.Components.Chat;

/// <summary>How a tool call reads in the transcript: a title, a one-line preview, the shell command
/// on its own, and the remaining arguments as labelled fields rather than a JSON blob.</summary>
public sealed record ToolCallView(
	string Icon,
	string Title,
	string? Preview,
	string? Command,
	IReadOnlyList<ToolCallField> Fields,
	string? RawInput);

/// <summary>One argument of a tool call. Block values (multi-line or long) render as code.</summary>
public sealed record ToolCallField(string Label, string Value, bool IsBlock);

/// <summary>
/// Turns a tool call's name and raw input (the provider's JSON arguments, or plain text) into a
/// <see cref="ToolCallView"/>. Claude, Copilot and OpenCode name their tools differently but pass the
/// same shapes: a shell tool takes <c>command</c> and <c>description</c>, a file tool a path.
/// </summary>
public static class ToolCallFormatter
{
	private const int MaxInlineLength = 80;
	private const int MaxBlockLength = 2000;

	private static readonly HashSet<string> ShellTools = new(StringComparer.OrdinalIgnoreCase)
	{
		"bash", "shell", "sh", "zsh", "powershell", "pwsh", "local_shell",
		"run_shell_command", "execute_command", "run_terminal_cmd"
	};

	/// <summary>Arguments that hold code or prose, shown as a block however short.</summary>
	private static readonly HashSet<string> BlockKeys = new(StringComparer.OrdinalIgnoreCase)
	{
		"old_string", "oldString", "old_str", "new_string", "newString", "new_str", "content", "file_text", "prompt"
	};

	private static readonly string[] PathKeys = ["file_path", "filePath", "path", "notebook_path", "absolute_path"];
	private static readonly string[] PatternKeys = ["pattern", "query", "regex"];

	/// <summary>Labels that read better than the humanized key.</summary>
	private static readonly Dictionary<string, string> FieldLabels = new(StringComparer.OrdinalIgnoreCase)
	{
		["old_string"] = "Find",
		["oldString"] = "Find",
		["old_str"] = "Find",
		["new_string"] = "Replace with",
		["newString"] = "Replace with",
		["new_str"] = "Replace with",
		["file_text"] = "Content",
		["subagent_type"] = "Agent"
	};

	public static ToolCallView Format(string? toolName, string? toolInput)
	{
		var name = string.IsNullOrWhiteSpace(toolName) ? "unknown_tool" : toolName.Trim();
		var kind = Classify(name);
		var input = toolInput?.Trim();

		if (string.IsNullOrEmpty(input))
		{
			return new ToolCallView(GetIcon(kind), name, null, null, [], null);
		}

		if (!TryParseObject(input, out var arguments))
		{
			var text = TryReadJsonString(input) ?? input;
			return kind == ToolKind.Shell
				? new ToolCallView(GetIcon(kind), name, FirstLine(text), text, [], null)
				: new ToolCallView(GetIcon(kind), name, FirstLine(text), null, [], text);
		}

		using (arguments)
		{
			var root = arguments.RootElement;
			var consumed = new HashSet<string>(StringComparer.Ordinal);
			var view = kind switch
			{
				ToolKind.Shell => FormatShell(name, root, consumed),
				ToolKind.Read or ToolKind.Edit or ToolKind.Write or ToolKind.List => FormatFile(name, kind, root, consumed),
				ToolKind.Search or ToolKind.FindFiles => FormatSearch(name, kind, root, consumed),
				ToolKind.Agent => FormatAgent(name, root, consumed),
				_ => null
			} ?? FormatGeneric(name, kind, root, consumed);

			var fields = view.Fields.Concat(BuildFields(root, consumed)).ToList();
			return view with { Fields = fields };
		}
	}

	private static ToolCallView? FormatShell(string name, JsonElement root, HashSet<string> consumed)
	{
		var command = ReadCommand(root);
		if (string.IsNullOrWhiteSpace(command))
		{
			return null;
		}

		consumed.Add("command");
		var description = ReadString(root, "description", consumed);
		return new ToolCallView(GetIcon(ToolKind.Shell), description ?? name, FirstLine(command), command, [], null);
	}

	private static ToolCallView? FormatFile(string name, ToolKind kind, JsonElement root, HashSet<string> consumed)
	{
		var path = ReadFirstString(root, PathKeys, consumed);
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}

		// Copilot's str_replace_editor names its action in a "command" argument.
		if (ReadString(root, "command", consumed) is { } action)
		{
			kind = action.ToLowerInvariant() switch
			{
				"view" => ToolKind.Read,
				"create" => ToolKind.Write,
				_ => ToolKind.Edit
			};
		}

		var verb = kind switch
		{
			ToolKind.Read => "Read",
			ToolKind.Write => "Write",
			ToolKind.List => "List",
			_ => "Edit"
		};
		var fields = new List<ToolCallField>();
		if (ReadLineRange(root, consumed) is { } lines)
		{
			fields.Add(new ToolCallField("Lines", lines, false));
		}

		return new ToolCallView(GetIcon(kind), $"{verb} {GetFileName(path)}", path, null, fields, null);
	}

	private static ToolCallView? FormatSearch(string name, ToolKind kind, JsonElement root, HashSet<string> consumed)
	{
		var pattern = ReadFirstString(root, PatternKeys, consumed);
		if (string.IsNullOrWhiteSpace(pattern))
		{
			return null;
		}

		var verb = kind == ToolKind.FindFiles ? "Find" : "Search";
		var path = ReadFirstString(root, PathKeys, consumed);
		return new ToolCallView(GetIcon(kind), $"{verb} “{FirstLine(pattern)}”", path, null, [], null);
	}

	private static ToolCallView? FormatAgent(string name, JsonElement root, HashSet<string> consumed)
	{
		var description = ReadString(root, "description", consumed);
		return description is null
			? null
			: new ToolCallView(GetIcon(ToolKind.Agent), description, name, null, [], null);
	}

	private static ToolCallView FormatGeneric(string name, ToolKind kind, JsonElement root, HashSet<string> consumed)
	{
		// The first short text argument (a URL, a query, a skill name) says what the call is about.
		string? preview = null;
		if (root.ValueKind == JsonValueKind.Object)
		{
			foreach (var property in root.EnumerateObject())
			{
				if (!consumed.Contains(property.Name)
					&& property.Value.ValueKind == JsonValueKind.String
					&& property.Value.GetString() is { Length: > 0 and <= MaxInlineLength } value
					&& !value.Contains('\n'))
				{
					preview = value;
					consumed.Add(property.Name);
					break;
				}
			}
		}

		return new ToolCallView(GetIcon(kind), name, preview, null, [], null);
	}

	private static IEnumerable<ToolCallField> BuildFields(JsonElement root, HashSet<string> consumed)
	{
		if (root.ValueKind != JsonValueKind.Object)
		{
			yield break;
		}

		foreach (var property in root.EnumerateObject())
		{
			if (consumed.Contains(property.Name))
			{
				continue;
			}

			var value = property.Value;
			string text;
			switch (value.ValueKind)
			{
				case JsonValueKind.Null:
				case JsonValueKind.Undefined:
				case JsonValueKind.False:
					// A false flag is almost always the default; listing it is noise.
					continue;
				case JsonValueKind.True:
					text = "Yes";
					break;
				case JsonValueKind.String:
					text = value.GetString() ?? string.Empty;
					if (text.Length == 0)
					{
						continue;
					}
					break;
				case JsonValueKind.Number:
					text = value.GetRawText();
					break;
				case JsonValueKind.Array when value.EnumerateArray().All(item => item.ValueKind is JsonValueKind.String or JsonValueKind.Number):
					text = string.Join(", ", value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText()));
					break;
				default:
					text = JsonSerializer.Serialize(value, IndentedJson);
					break;
			}

			var isBlock = BlockKeys.Contains(property.Name) || text.Contains('\n') || text.Length > MaxInlineLength;
			if (text.Length > MaxBlockLength)
			{
				text = text[..MaxBlockLength] + "\n… (truncated)";
			}

			yield return new ToolCallField(GetLabel(property.Name), text, isBlock);
		}
	}

	private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

	private static string? ReadCommand(JsonElement root)
	{
		if (!root.TryGetProperty("command", out var command))
		{
			return null;
		}

		return command.ValueKind switch
		{
			JsonValueKind.String => command.GetString(),
			// Some shells take argv: ["bash", "-lc", "git status"].
			JsonValueKind.Array => string.Join(" ", command.EnumerateArray()
				.Where(item => item.ValueKind == JsonValueKind.String)
				.Select(item => item.GetString())),
			_ => null
		};
	}

	private static string? ReadLineRange(JsonElement root, HashSet<string> consumed)
	{
		if (root.TryGetProperty("view_range", out var range)
			&& range.ValueKind == JsonValueKind.Array
			&& range.GetArrayLength() == 2
			&& range[0].TryGetInt32(out var first)
			&& range[1].TryGetInt32(out var last))
		{
			consumed.Add("view_range");
			return last < 0 ? $"{first}–end" : $"{first}–{last}";
		}

		var hasOffset = TryReadInt(root, "offset", out var offset);
		var hasLimit = TryReadInt(root, "limit", out var limit);
		if (!hasOffset && !hasLimit)
		{
			return null;
		}

		consumed.Add("offset");
		consumed.Add("limit");
		var start = hasOffset ? offset : 1;
		return hasLimit ? $"{start}–{start + limit - 1}" : $"{start}–end";
	}

	private static bool TryReadInt(JsonElement root, string key, out int value)
	{
		value = 0;
		return root.TryGetProperty(key, out var property)
			&& property.ValueKind == JsonValueKind.Number
			&& property.TryGetInt32(out value);
	}

	private static string? ReadFirstString(JsonElement root, IEnumerable<string> keys, HashSet<string> consumed)
	{
		foreach (var key in keys)
		{
			if (ReadString(root, key, consumed) is { } value)
			{
				return value;
			}
		}

		return null;
	}

	private static string? ReadString(JsonElement root, string key, HashSet<string> consumed)
	{
		if (!root.TryGetProperty(key, out var property)
			|| property.ValueKind != JsonValueKind.String
			|| string.IsNullOrWhiteSpace(property.GetString()))
		{
			return null;
		}

		consumed.Add(key);
		return property.GetString()!.Trim();
	}

	private static bool TryParseObject(string input, out JsonDocument document)
	{
		document = null!;
		if (!input.StartsWith('{'))
		{
			return false;
		}

		try
		{
			document = JsonDocument.Parse(input);
			if (document.RootElement.ValueKind == JsonValueKind.Object)
			{
				return true;
			}

			document.Dispose();
		}
		catch (JsonException)
		{
		}

		return false;
	}

	private static string? TryReadJsonString(string input)
	{
		if (!input.StartsWith('"'))
		{
			return null;
		}

		try
		{
			return JsonSerializer.Deserialize<string>(input);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static string FirstLine(string text)
	{
		var trimmed = text.Trim();
		var newline = trimmed.IndexOf('\n');
		return newline < 0 ? trimmed : trimmed[..newline].TrimEnd() + " …";
	}

	private static string GetFileName(string path)
	{
		var trimmed = path.TrimEnd('/', '\\');
		var slash = trimmed.LastIndexOfAny(['/', '\\']);
		return slash < 0 || slash == trimmed.Length - 1 ? trimmed : trimmed[(slash + 1)..];
	}

	private static string GetLabel(string key)
	{
		if (FieldLabels.TryGetValue(key, out var label))
		{
			return label;
		}

		// file_path, filePath and run-in-background all read as "File path"-style labels.
		var words = new StringBuilder(key.Length + 4);
		for (var index = 0; index < key.Length; index++)
		{
			var character = key[index];
			if (character is '_' or '-')
			{
				words.Append(' ');
			}
			else if (char.IsUpper(character) && index > 0 && !char.IsUpper(key[index - 1]))
			{
				words.Append(' ').Append(char.ToLowerInvariant(character));
			}
			else
			{
				words.Append(words.Length == 0 ? char.ToUpperInvariant(character) : character);
			}
		}

		return words.ToString();
	}

	private static ToolKind Classify(string name)
	{
		if (ShellTools.Contains(name))
		{
			return ToolKind.Shell;
		}

		return name.ToLowerInvariant() switch
		{
			"read" or "view" or "read_file" or "notebookread" => ToolKind.Read,
			"edit" or "multiedit" or "str_replace_editor" or "str_replace_based_edit_tool" or "notebookedit" or "apply_patch" => ToolKind.Edit,
			"write" or "create" or "write_file" => ToolKind.Write,
			"ls" or "list" or "list_dir" => ToolKind.List,
			"grep" or "rg" or "search" or "codebase_search" or "search_files" => ToolKind.Search,
			"glob" or "find" or "file_search" => ToolKind.FindFiles,
			"webfetch" or "web_fetch" or "fetch" or "websearch" or "web_search" => ToolKind.Web,
			"task" or "agent" => ToolKind.Agent,
			"todowrite" or "todo_write" or "update_todo" => ToolKind.Todo,
			"skill" => ToolKind.Skill,
			_ => ToolKind.Other
		};
	}

	private static string GetIcon(ToolKind kind) => kind switch
	{
		ToolKind.Shell => "terminal",
		ToolKind.Read => "file-earmark-text",
		ToolKind.Edit => "pencil-square",
		ToolKind.Write => "file-earmark-plus",
		ToolKind.List => "folder2-open",
		ToolKind.Search or ToolKind.FindFiles => "search",
		ToolKind.Web => "globe",
		ToolKind.Agent => "diagram-3",
		ToolKind.Todo => "list-check",
		ToolKind.Skill => "stars",
		_ => "tools"
	};

	private enum ToolKind
	{
		Other,
		Shell,
		Read,
		Edit,
		Write,
		List,
		Search,
		FindFiles,
		Web,
		Agent,
		Todo,
		Skill
	}
}
