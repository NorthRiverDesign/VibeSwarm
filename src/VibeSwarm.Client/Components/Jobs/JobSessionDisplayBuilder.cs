using System.Text.Json;
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
		bool isJobActive,
		IEnumerable<JobMessage>? liveMessages = null)
	{
		var persisted = NormalizePersistedMessages(persistedMessages);
		var liveTranscript = BuildMessagesFromOutput(outputLines);
		var supplementalMessages = NormalizePersistedMessages(liveMessages);

		if (isJobActive)
		{
			if (liveTranscript.Count > 0)
			{
				return MergeMessages(liveTranscript, supplementalMessages);
			}

			if (persisted.Count > 0)
			{
				return MergeMessages(persisted, supplementalMessages);
			}

			return supplementalMessages;
		}

		if (persisted.Count == 0)
		{
			return MergeMessages(liveTranscript, supplementalMessages);
		}

		var persistedHasStructuredMessages = HasStructuredMessages(persisted);
		var liveHasStructuredMessages = HasStructuredMessages(liveTranscript);

		if (!persistedHasStructuredMessages && liveTranscript.Count > persisted.Count)
		{
			return MergeMessages(liveTranscript, supplementalMessages);
		}

		if (!persistedHasStructuredMessages && liveHasStructuredMessages && liveTranscript.Count >= persisted.Count)
		{
			return MergeMessages(liveTranscript, supplementalMessages);
		}

		return MergeMessages(persisted, supplementalMessages);
	}

	private static List<JobMessage> NormalizePersistedMessages(IEnumerable<JobMessage>? persistedMessages)
	{
		if (persistedMessages == null)
		{
			return [];
		}

		var normalizedMessages = new List<JobMessage>();
		var pendingToolNames = new Queue<string>();

		foreach (var message in persistedMessages.OrderBy(message => message.CreatedAt))
		{
			normalizedMessages.Add(NormalizePersistedMessage(message, pendingToolNames));
		}

		return normalizedMessages;
	}

	private static JobMessage NormalizePersistedMessage(JobMessage message, Queue<string> pendingToolNames)
	{
		var normalized = new JobMessage
		{
			Id = message.Id,
			JobId = message.JobId,
			Job = message.Job,
			Role = message.Role,
			Content = message.Content,
			CreatedAt = message.CreatedAt,
			ToolName = message.ToolName,
			ToolInput = message.ToolInput,
			ToolOutput = message.ToolOutput,
			TokenCount = message.TokenCount
		};

		switch (message.Role)
		{
			case MessageRole.User:
				normalized.Source = MessageSource.User;
				break;

			case MessageRole.Assistant:
				normalized.Source = MessageSource.Provider;
				break;

			case MessageRole.ToolUse:
				normalized.Source = MessageSource.Provider;
				if (!string.IsNullOrWhiteSpace(normalized.ToolName))
				{
					pendingToolNames.Enqueue(normalized.ToolName);
				}
				break;

			case MessageRole.ToolResult:
				normalized.Source = MessageSource.Provider;
				NormalizeToolResultName(normalized, pendingToolNames);
				break;

			case MessageRole.System:
				ApplySystemDisplayMetadata(normalized, message.Content);
				break;
		}

		return normalized;
	}

	private static void ApplySystemDisplayMetadata(JobMessage message, string? content)
	{
		if (string.IsNullOrWhiteSpace(content))
		{
			message.Role = MessageRole.Assistant;
			message.Source = MessageSource.Provider;
			message.Level = MessageLevel.Normal;
			return;
		}

		var trimmedContent = content.Trim();
		if (TryParseSystemMessage(trimmedContent, out _, out var source, out var level))
		{
			message.Role = MessageRole.System;
			message.Source = source;
			message.Level = level;
			return;
		}

		if (trimmedContent.StartsWith("[Plan]", StringComparison.OrdinalIgnoreCase)
			|| trimmedContent.StartsWith("[Reasoning]", StringComparison.OrdinalIgnoreCase)
			|| trimmedContent.StartsWith("[Thinking]", StringComparison.OrdinalIgnoreCase))
		{
			message.Role = MessageRole.System;
			message.Source = MessageSource.Provider;
			message.Level = MessageLevel.Normal;
			return;
		}

		if (trimmedContent.StartsWith("[Assistant]", StringComparison.OrdinalIgnoreCase))
		{
			message.Content = StripKnownPrefix(trimmedContent, "[Assistant]");
		}

		message.Role = MessageRole.Assistant;
		message.Source = MessageSource.Provider;
		message.Level = MessageLevel.Normal;
	}

	private static void NormalizeToolResultName(JobMessage message, Queue<string> pendingToolNames)
	{
		if (pendingToolNames.Count == 0)
		{
			return;
		}

		var pendingToolName = pendingToolNames.Peek();
		if (string.IsNullOrWhiteSpace(message.ToolName) || LooksLikeGeneratedToolIdentifier(message.ToolName))
		{
			message.ToolName = pendingToolName;
			pendingToolNames.Dequeue();
			return;
		}

		if (string.Equals(message.ToolName, pendingToolName, StringComparison.OrdinalIgnoreCase))
		{
			pendingToolNames.Dequeue();
		}
	}

	private static bool LooksLikeGeneratedToolIdentifier(string value)
	{
		if (Guid.TryParse(value, out _))
		{
			return true;
		}

		return value.StartsWith("toolu_", StringComparison.OrdinalIgnoreCase)
			|| value.StartsWith("call_", StringComparison.OrdinalIgnoreCase)
			|| value.StartsWith("tool_", StringComparison.OrdinalIgnoreCase);
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

			if (IsDuplicateProcessStartedMessage(messages.LastOrDefault(), parsedMessage))
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

		// Detect raw JSON stream events and extract human-readable content
		if (content.StartsWith('{') && TryExtractFromStreamJson(content, line.Timestamp, out var jsonMessage))
		{
			return jsonMessage;
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
				Source = MessageSource.Provider,
				Level = MessageLevel.Normal,
				CreatedAt = line.Timestamp
			};
		}

		if (TryParseSystemMessage(content, out var systemContent, out var source, out var level))
		{
			return new JobMessage
			{
				Role = MessageRole.System,
				Content = systemContent,
				Source = source,
				Level = level,
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
				Source = MessageSource.Provider,
				Level = MessageLevel.Normal,
				CreatedAt = line.Timestamp
			};
		}

		if (line.IsError)
		{
			return new JobMessage
			{
				Role = MessageRole.System,
				Content = content,
				Source = MessageSource.System,
				Level = MessageLevel.Error,
				CreatedAt = line.Timestamp
			};
		}

		return new JobMessage
		{
			Role = MessageRole.Assistant,
			Content = content,
			Source = MessageSource.Provider,
			Level = MessageLevel.Normal,
			CreatedAt = line.Timestamp
		};
	}

	private static bool HasStructuredMessages(IReadOnlyCollection<JobMessage> messages)
		=> messages.Any(message => message.Role is MessageRole.ToolUse or MessageRole.ToolResult or MessageRole.System)
			|| messages.Count > 1;

	private static bool IsDuplicateProcessStartedMessage(JobMessage? previousMessage, JobMessage currentMessage)
	{
		if (previousMessage == null
			|| previousMessage.Role != MessageRole.System
			|| currentMessage.Role != MessageRole.System)
		{
			return false;
		}

		if (!string.Equals(previousMessage.Content, currentMessage.Content, StringComparison.Ordinal))
		{
			return false;
		}

		return currentMessage.Content.StartsWith("Process started (PID:", StringComparison.Ordinal);
	}

	private static IReadOnlyList<JobMessage> MergeMessages(
		IReadOnlyCollection<JobMessage> primaryMessages,
		IReadOnlyCollection<JobMessage> supplementalMessages)
	{
		if (supplementalMessages.Count == 0)
		{
			return [.. primaryMessages];
		}

		if (primaryMessages.Count == 0)
		{
			return [.. supplementalMessages];
		}

		return primaryMessages
			.Concat(supplementalMessages)
			.OrderBy(message => message.CreatedAt)
			.ToList();
	}

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

	/// <summary>
	/// Attempts to extract a human-readable message from a raw JSON stream event.
	/// Returns null for events that should be hidden from the Session Log (e.g., init events).
	/// </summary>
	private static bool TryExtractFromStreamJson(string json, DateTime timestamp, out JobMessage? message)
	{
		message = null;

		try
		{
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;

			if (!root.TryGetProperty("type", out var typeProp))
			{
				return false;
			}

			var type = typeProp.GetString();

			switch (type)
			{
				case "system":
					// Init events are noise for the Session Log — skip them
					return true;

				case "assistant":
				{
					// Skip error responses — the "result" event contains the same text
					// and is the authoritative final message. Showing both would duplicate.
					if (root.TryGetProperty("error", out _))
					{
						return true;
					}

					var text = ExtractAssistantText(root);
					if (string.IsNullOrWhiteSpace(text))
					{
						return true;
					}

					message = new JobMessage
					{
						Role = MessageRole.Assistant,
						Content = text,
						Source = MessageSource.Provider,
						Level = MessageLevel.Normal,
						CreatedAt = timestamp
					};
					return true;
				}

				case "result":
				{
					var resultText = root.TryGetProperty("result", out var resultProp)
						? resultProp.GetString()
						: null;

					if (string.IsNullOrWhiteSpace(resultText))
					{
						return true;
					}

					var isError = root.TryGetProperty("is_error", out var isErrorProp)
						&& isErrorProp.GetBoolean();

					message = new JobMessage
					{
						Role = isError ? MessageRole.System : MessageRole.Assistant,
						Content = resultText,
						Source = MessageSource.Provider,
						Level = isError ? MessageLevel.Error : MessageLevel.Normal,
						CreatedAt = timestamp
					};
					return true;
				}

				default:
					// Unknown JSON event type — skip to avoid showing raw JSON
					return true;
			}
		}
		catch (JsonException)
		{
			// Not valid JSON — fall through to normal text parsing
			return false;
		}
	}

	private static string? ExtractAssistantText(JsonElement root)
	{
		if (!root.TryGetProperty("message", out var messageProp))
		{
			return null;
		}

		if (!messageProp.TryGetProperty("content", out var contentProp)
			|| contentProp.ValueKind != JsonValueKind.Array)
		{
			return null;
		}

		foreach (var block in contentProp.EnumerateArray())
		{
			if (block.TryGetProperty("type", out var blockType)
				&& blockType.GetString() == "text"
				&& block.TryGetProperty("text", out var textProp))
			{
				var text = textProp.GetString();
				if (!string.IsNullOrWhiteSpace(text))
				{
					return text;
				}
			}
		}

		return null;
	}

	private static bool IsCliWaitStatus(string content)
		=> content.StartsWith("[System] Still initializing...", StringComparison.OrdinalIgnoreCase)
			|| content.StartsWith("[System] Still waiting for response...", StringComparison.OrdinalIgnoreCase)
			|| content.StartsWith("[System] Still waiting (", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Matches bracketed prefix messages, strips the prefix, and assigns Source/Level.
	/// </summary>
	private static bool TryParseSystemMessage(string content, out string displayContent, out MessageSource source, out MessageLevel level)
	{
		displayContent = content;
		source = MessageSource.System;
		level = MessageLevel.Normal;

		// Map prefixes to Source + Level
		if (content.StartsWith("[System]", StringComparison.OrdinalIgnoreCase))
		{
			displayContent = content[8..].Trim();
			source = MessageSource.System;
			level = MessageLevel.Normal;
			return true;
		}

		if (content.StartsWith("[Connection]", StringComparison.OrdinalIgnoreCase))
		{
			displayContent = content[12..].Trim();
			source = MessageSource.System;
			level = MessageLevel.Warning;
			return true;
		}

		if (content.StartsWith("[Retry]", StringComparison.OrdinalIgnoreCase))
		{
			displayContent = content[7..].Trim();
			source = MessageSource.Provider;
			level = MessageLevel.Warning;
			return true;
		}

		if (content.StartsWith("[Session]", StringComparison.OrdinalIgnoreCase))
		{
			var body = content[9..].Trim();
			displayContent = body;
			source = MessageSource.Provider;
			level = body.StartsWith("Complete", StringComparison.OrdinalIgnoreCase)
				? MessageLevel.Success
				: MessageLevel.Normal;
			return true;
		}

		if (content.StartsWith("[Error]", StringComparison.OrdinalIgnoreCase))
		{
			displayContent = content[7..].Trim();
			source = MessageSource.Provider;
			level = MessageLevel.Error;
			return true;
		}

		return false;
	}
}
