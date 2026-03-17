using VibeSwarm.Client.Models;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Client.Components.Jobs;

internal static class JobSessionDisplayBuilder
{
	public static OutputLine CreateOutputLine(string rawLine, DateTime timestamp)
	{
		var isError = rawLine.StartsWith("[ERR] ", StringComparison.Ordinal);
		var content = isError ? rawLine[6..] : rawLine;
		var isThinking = content.StartsWith("[Reasoning]", StringComparison.OrdinalIgnoreCase)
			|| content.StartsWith("[Thinking]", StringComparison.OrdinalIgnoreCase);
		var contentCategory = isThinking
			? "thinking"
			: content.StartsWith("[Tool]", StringComparison.OrdinalIgnoreCase)
				|| content.StartsWith("[Tool Result]", StringComparison.OrdinalIgnoreCase)
				? "tool"
				: "text";

		return new OutputLine
		{
			Content = content.TrimEnd('\r'),
			IsError = isError,
			Timestamp = timestamp,
			IsThinking = isThinking,
			ContentCategory = contentCategory
		};
	}

	public static IReadOnlyList<JobMessage> BuildDisplayedMessages(
		IEnumerable<JobMessage>? persistedMessages,
		IEnumerable<OutputLine>? outputLines,
		bool isJobActive)
	{
		var persisted = persistedMessages?
			.OrderBy(message => message.CreatedAt)
			.ToList() ?? [];
		var liveTranscript = BuildMessagesFromOutput(outputLines);

		if (isJobActive)
		{
			return liveTranscript.Count > 0 ? liveTranscript : persisted;
		}

		if (persisted.Count == 0)
		{
			return liveTranscript;
		}

		var persistedHasStructuredMessages = HasStructuredMessages(persisted);
		var liveHasStructuredMessages = HasStructuredMessages(liveTranscript);

		if (!persistedHasStructuredMessages && liveTranscript.Count > persisted.Count)
		{
			return liveTranscript;
		}

		if (!persistedHasStructuredMessages && liveHasStructuredMessages && liveTranscript.Count >= persisted.Count)
		{
			return liveTranscript;
		}

		return persisted;
	}

	private static List<JobMessage> BuildMessagesFromOutput(IEnumerable<OutputLine>? outputLines)
	{
		if (outputLines == null)
		{
			return [];
		}

		var messages = new List<JobMessage>();
		JobMessage? currentTextMessage = null;

		foreach (var line in outputLines.OrderBy(line => line.Timestamp))
		{
			if (string.IsNullOrWhiteSpace(line.Content))
			{
				continue;
			}

			var parsedMessage = ParseOutputLine(line);
			if (parsedMessage == null)
			{
				continue;
			}

			var isTextMessage = parsedMessage.Role is MessageRole.Assistant
				&& string.IsNullOrEmpty(parsedMessage.ToolName)
				&& string.IsNullOrEmpty(parsedMessage.ToolInput)
				&& string.IsNullOrEmpty(parsedMessage.ToolOutput);

			if (isTextMessage
				&& currentTextMessage != null
				&& currentTextMessage.Role == parsedMessage.Role
				&& !line.IsError)
			{
				currentTextMessage.Content = string.Concat(
					currentTextMessage.Content,
					Environment.NewLine,
					parsedMessage.Content);
				continue;
			}

			messages.Add(parsedMessage);
			currentTextMessage = isTextMessage ? parsedMessage : null;
		}

		return messages;
	}

	private static JobMessage? ParseOutputLine(OutputLine line)
	{
		var content = line.Content.Trim();
		if (content.Length == 0)
		{
			return null;
		}

		if (IsCliWaitStatus(content))
		{
			return null;
		}

		if (TryParseToolUse(content, out var toolName, out var toolInput))
		{
			return new JobMessage
			{
				Role = MessageRole.ToolUse,
				Content = toolName,
				ToolName = toolName,
				ToolInput = toolInput,
				CreatedAt = line.Timestamp
			};
		}

		if (TryParseToolResult(content, out toolName, out var toolOutput))
		{
			return new JobMessage
			{
				Role = MessageRole.ToolResult,
				Content = toolOutput ?? string.Empty,
				ToolName = toolName,
				ToolOutput = toolOutput,
				CreatedAt = line.Timestamp
			};
		}

		if (line.IsThinking)
		{
			return new JobMessage
			{
				Role = MessageRole.System,
				Content = StripKnownPrefix(content, "[Reasoning]", "[Thinking]"),
				CreatedAt = line.Timestamp
			};
		}

		if (IsSystemStatusMessage(content))
		{
			return new JobMessage
			{
				Role = MessageRole.System,
				Content = content,
				CreatedAt = line.Timestamp
			};
		}

		if (content.StartsWith("[Assistant]", StringComparison.OrdinalIgnoreCase))
		{
			content = StripKnownPrefix(content, "[Assistant]");
		}

		if (content.StartsWith("[Plan]", StringComparison.OrdinalIgnoreCase))
		{
			return new JobMessage
			{
				Role = MessageRole.System,
				Content = StripKnownPrefix(content, "[Plan]"),
				CreatedAt = line.Timestamp
			};
		}

		if (line.IsError)
		{
			return new JobMessage
			{
				Role = MessageRole.System,
				Content = content,
				CreatedAt = line.Timestamp
			};
		}

		return new JobMessage
		{
			Role = MessageRole.Assistant,
			Content = content,
			CreatedAt = line.Timestamp
		};
	}

	private static bool HasStructuredMessages(IReadOnlyCollection<JobMessage> messages)
		=> messages.Any(message => message.Role is MessageRole.ToolUse or MessageRole.ToolResult or MessageRole.System)
			|| messages.Count > 1;

	private static bool TryParseToolUse(string content, out string toolName, out string? toolInput)
	{
		return TryParseToolLine(content, "[Tool]", out toolName, out toolInput);
	}

	private static bool TryParseToolResult(string content, out string toolName, out string? toolOutput)
	{
		return TryParseToolLine(content, "[Tool Result]", out toolName, out toolOutput);
	}

	private static bool TryParseToolLine(string content, string prefix, out string toolName, out string? payload)
	{
		toolName = string.Empty;
		payload = null;

		if (!content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		var remainder = content[prefix.Length..].Trim();
		if (remainder.Length == 0)
		{
			toolName = "unknown_tool";
			return true;
		}

		var separatorIndex = remainder.IndexOf(": ", StringComparison.Ordinal);
		if (separatorIndex >= 0)
		{
			toolName = remainder[..separatorIndex].Trim();
			payload = remainder[(separatorIndex + 2)..].Trim();
		}
		else
		{
			toolName = remainder;
		}

		if (string.IsNullOrWhiteSpace(toolName))
		{
			toolName = "unknown_tool";
		}

		return true;
	}

	private static string StripKnownPrefix(string content, params string[] prefixes)
	{
		foreach (var prefix in prefixes)
		{
			if (content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				return content[prefix.Length..].Trim();
			}
		}

		return content;
	}

	private static bool IsCliWaitStatus(string content)
		=> content.StartsWith("[VibeSwarm] Still initializing...", StringComparison.OrdinalIgnoreCase)
			|| content.StartsWith("[VibeSwarm] Still waiting for response...", StringComparison.OrdinalIgnoreCase)
			|| content.StartsWith("[VibeSwarm] Still waiting (", StringComparison.OrdinalIgnoreCase);

	private static bool IsSystemStatusMessage(string content)
		=> content.StartsWith("[VibeSwarm]", StringComparison.OrdinalIgnoreCase)
			|| content.StartsWith("[Connection]", StringComparison.OrdinalIgnoreCase)
			|| content.StartsWith("[Retry]", StringComparison.OrdinalIgnoreCase)
			|| content.StartsWith("[Session]", StringComparison.OrdinalIgnoreCase)
			|| content.StartsWith("[Error]", StringComparison.OrdinalIgnoreCase);
}
