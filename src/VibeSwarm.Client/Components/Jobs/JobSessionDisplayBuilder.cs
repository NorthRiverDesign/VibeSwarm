using System.Text.Json;
using VibeSwarm.Client.Models;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

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
				return SanitizeDisplayedMessages(MergeMessages(liveTranscript, supplementalMessages));
			}

			if (persisted.Count > 0)
			{
				return SanitizeDisplayedMessages(MergeMessages(persisted, supplementalMessages));
			}

			return SanitizeDisplayedMessages(supplementalMessages);
		}

		if (persisted.Count == 0)
		{
			return SanitizeDisplayedMessages(MergeMessages(liveTranscript, supplementalMessages));
		}

		var persistedHasStructuredMessages = HasStructuredMessages(persisted);
		var liveHasStructuredMessages = HasStructuredMessages(liveTranscript);

		if (!persistedHasStructuredMessages && liveTranscript.Count > persisted.Count)
		{
			return SanitizeDisplayedMessages(MergeMessages(liveTranscript, supplementalMessages));
		}

		if (!persistedHasStructuredMessages && liveHasStructuredMessages && liveTranscript.Count >= persisted.Count)
		{
			return SanitizeDisplayedMessages(MergeMessages(liveTranscript, supplementalMessages));
		}

		return SanitizeDisplayedMessages(MergeMessages(persisted, supplementalMessages));
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
			TokenCount = message.TokenCount,
			DisplayVariant = message.DisplayVariant
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
		if (TryParseSystemMessage(trimmedContent, out var systemContent, out var source, out var level))
		{
			message.Content = systemContent;
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
			message.DisplayVariant = MessageDisplayVariant.Thinking;
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
		var parserState = new LiveTranscriptParserState();

		foreach (var line in outputLines.OrderBy(line => line.Timestamp))
		{
			if (string.IsNullOrWhiteSpace(line.Content))
			{
				continue;
			}

			var parsedMessages = ParseOutputMessages(line, parserState);
			if (parsedMessages.Count == 0)
			{
				continue;
			}

			foreach (var parsedMessage in parsedMessages)
			{
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
		}

		return messages;
	}

	private static IReadOnlyList<JobMessage> ParseOutputMessages(OutputLine line, LiveTranscriptParserState parserState)
	{
		var content = line.Content.Trim();
		if (content.Length == 0)
		{
			return [];
		}

		if (IsCliWaitStatus(content))
		{
			return [];
		}

		if (content.StartsWith('{') && TryExtractMessagesFromStreamJson(content, line.Timestamp, parserState, out var jsonMessages))
		{
			return jsonMessages;
		}

		if (TryParseToolUse(content, out var toolName, out var toolInput))
		{
			parserState.RegisterToolUse(null, toolName);
			return
			[
				new JobMessage
				{
					Role = MessageRole.ToolUse,
					Content = toolName,
					ToolName = toolName,
					ToolInput = toolInput,
					CreatedAt = line.Timestamp
				}
			];
		}

		if (TryParseToolResult(content, out toolName, out var toolOutput))
		{
			var resolvedToolName = parserState.ResolveToolName(toolName);
			return
			[
				new JobMessage
				{
					Role = MessageRole.ToolResult,
					Content = toolOutput ?? string.Empty,
					ToolName = resolvedToolName,
					ToolOutput = toolOutput,
					CreatedAt = line.Timestamp
				}
			];
		}

		if (line.IsThinking)
		{
			return
			[
				new JobMessage
				{
					Role = MessageRole.System,
					Content = StripKnownPrefix(content, "[Reasoning]", "[Thinking]"),
					Source = MessageSource.Provider,
					Level = MessageLevel.Normal,
					DisplayVariant = MessageDisplayVariant.Thinking,
					CreatedAt = line.Timestamp
				}
			];
		}

		if (TryParseSystemMessage(content, out var systemContent, out var source, out var level))
		{
			return
			[
				new JobMessage
				{
					Role = MessageRole.System,
					Content = systemContent,
					Source = source,
					Level = level,
					CreatedAt = line.Timestamp
				}
			];
		}

		if (content.StartsWith("[Assistant]", StringComparison.OrdinalIgnoreCase))
		{
			content = StripKnownPrefix(content, "[Assistant]");
		}

		if (content.StartsWith("[Plan]", StringComparison.OrdinalIgnoreCase))
		{
			return
			[
				new JobMessage
				{
					Role = MessageRole.System,
					Content = StripKnownPrefix(content, "[Plan]"),
					Source = MessageSource.Provider,
					Level = MessageLevel.Normal,
					DisplayVariant = MessageDisplayVariant.Thinking,
					CreatedAt = line.Timestamp
				}
			];
		}

		if (line.IsError)
		{
			return
			[
				new JobMessage
				{
					Role = MessageRole.System,
					Content = content,
					Source = MessageSource.System,
					Level = MessageLevel.Error,
					CreatedAt = line.Timestamp
				}
			];
		}

		return
		[
			new JobMessage
			{
				Role = MessageRole.Assistant,
				Content = content,
				Source = MessageSource.Provider,
				Level = MessageLevel.Normal,
				CreatedAt = line.Timestamp
			}
		];
	}

	private static bool HasStructuredMessages(IReadOnlyCollection<JobMessage> messages)
		=> messages.Any(message => message.Role is MessageRole.ToolUse or MessageRole.ToolResult or MessageRole.System)
			|| messages.Count > 1;

	private static bool IsDuplicateProcessStartedMessage(JobMessage? previousMessage, JobMessage currentMessage)
	{
		if (previousMessage == null)
		{
			return false;
		}

		return IsProcessStartedMessage(previousMessage) && IsProcessStartedMessage(currentMessage);
	}

	private static bool IsProcessStartedMessage(JobMessage message)
		=> message.Role == MessageRole.System
			&& (message.Content?.StartsWith("Process started (PID:", StringComparison.Ordinal) == true);

	private static bool IsRetryOrErrorMessage(JobMessage message)
		=> message.Role == MessageRole.System
			&& (message.Level == MessageLevel.Error || message.Level == MessageLevel.Warning);

	private static IReadOnlyList<JobMessage> MergeMessages(
		IReadOnlyCollection<JobMessage> primaryMessages,
		IReadOnlyCollection<JobMessage> supplementalMessages)
	{
		if (supplementalMessages.Count == 0)
		{
			return DeduplicateProcessStartedMessages(primaryMessages);
		}

		if (primaryMessages.Count == 0)
		{
			return DeduplicateProcessStartedMessages(supplementalMessages);
		}

		return DeduplicateProcessStartedMessages(primaryMessages
			.Concat(supplementalMessages)
			.OrderBy(message => message.CreatedAt)
			.ToList());
	}

	private static IReadOnlyList<JobMessage> DeduplicateProcessStartedMessages(IEnumerable<JobMessage> messages)
	{
		var deduplicatedMessages = new List<JobMessage>();
		var hasSeenProcessStart = false;
		var retryOrErrorSinceLastProcessStart = false;

		foreach (var message in messages)
		{
			if (IsProcessStartedMessage(message))
			{
				if (hasSeenProcessStart && !retryOrErrorSinceLastProcessStart)
				{
					continue;
				}

				hasSeenProcessStart = true;
				retryOrErrorSinceLastProcessStart = false;
			}
			else if (hasSeenProcessStart && IsRetryOrErrorMessage(message))
			{
				retryOrErrorSinceLastProcessStart = true;
			}

			deduplicatedMessages.Add(message);
		}

		return deduplicatedMessages;
	}

	private static IReadOnlyList<JobMessage> SanitizeDisplayedMessages(IReadOnlyCollection<JobMessage> messages)
	{
		var sanitizedMessages = new List<JobMessage>(messages.Count);

		foreach (var message in messages)
		{
			if (message.Role is not MessageRole.Assistant and not MessageRole.System)
			{
				sanitizedMessages.Add(message);
				continue;
			}

			var sanitizedContent = JobSummaryGenerator.StripCommitSummary(message.Content);
			if (string.IsNullOrWhiteSpace(sanitizedContent))
			{
				continue;
			}

			if (string.Equals(sanitizedContent, message.Content, StringComparison.Ordinal))
			{
				sanitizedMessages.Add(message);
				continue;
			}

			sanitizedMessages.Add(new JobMessage
			{
				Id = message.Id,
				JobId = message.JobId,
				Job = message.Job,
				Role = message.Role,
				Content = sanitizedContent,
				CreatedAt = message.CreatedAt,
				ToolName = message.ToolName,
				ToolInput = message.ToolInput,
				ToolOutput = message.ToolOutput,
				TokenCount = message.TokenCount,
				Source = message.Source,
				Level = message.Level,
				DisplayVariant = message.DisplayVariant
			});
		}

		return sanitizedMessages;
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

	private static bool TryExtractMessagesFromStreamJson(
		string json,
		DateTime timestamp,
		LiveTranscriptParserState parserState,
		out IReadOnlyList<JobMessage> messages)
	{
		messages = [];

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
					return true;

				case "assistant":
				{
					if (root.TryGetProperty("error", out _))
					{
						return true;
					}

					messages = ExtractAssistantMessages(root, timestamp, parserState);
					return true;
				}

				case "user":
					messages = ExtractUserMessages(root, timestamp, parserState);
					return true;

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

					messages =
					[
						new JobMessage
						{
							Role = isError ? MessageRole.System : MessageRole.Assistant,
							Content = resultText,
							Source = MessageSource.Provider,
							Level = isError ? MessageLevel.Error : MessageLevel.Normal,
							CreatedAt = timestamp
						}
					];
					return true;
				}

				default:
					return true;
			}
		}
		catch (JsonException)
		{
			return false;
		}
	}

	private static IReadOnlyList<JobMessage> ExtractAssistantMessages(
		JsonElement root,
		DateTime timestamp,
		LiveTranscriptParserState parserState)
	{
		if (!root.TryGetProperty("message", out var messageProp))
		{
			return [];
		}

		if (!messageProp.TryGetProperty("content", out var contentProp)
			|| contentProp.ValueKind != JsonValueKind.Array)
		{
			return [];
		}

		var messages = new List<JobMessage>();

		foreach (var block in contentProp.EnumerateArray())
		{
			if (!block.TryGetProperty("type", out var blockType))
			{
				continue;
			}

			switch (blockType.GetString())
			{
				case "text":
				{
					var text = block.TryGetProperty("text", out var textProp)
						? textProp.GetString()
						: null;
					if (!string.IsNullOrWhiteSpace(text))
					{
						messages.Add(new JobMessage
						{
							Role = MessageRole.Assistant,
							Content = text,
							Source = MessageSource.Provider,
							Level = MessageLevel.Normal,
							CreatedAt = timestamp
						});
					}

					break;
				}

				case "thinking":
				{
					var thinking = block.TryGetProperty("thinking", out var thinkingProp)
						? thinkingProp.GetString()
						: null;
					if (!string.IsNullOrWhiteSpace(thinking))
					{
						messages.Add(new JobMessage
						{
							Role = MessageRole.System,
							Content = thinking,
							Source = MessageSource.Provider,
							Level = MessageLevel.Normal,
							DisplayVariant = MessageDisplayVariant.Thinking,
							CreatedAt = timestamp
						});
					}

					break;
				}

				case "tool_use":
				{
					var toolId = block.TryGetProperty("id", out var idProp)
						? idProp.GetString()
						: null;
					var toolName = block.TryGetProperty("name", out var nameProp)
						? nameProp.GetString()
						: null;
					var toolInput = block.TryGetProperty("input", out var inputProp)
						? inputProp.GetRawText()
						: null;
					var displayName = string.IsNullOrWhiteSpace(toolName) ? "unknown_tool" : toolName;

					parserState.RegisterToolUse(toolId, displayName);
					messages.Add(new JobMessage
					{
						Role = MessageRole.ToolUse,
						Content = displayName,
						ToolName = displayName,
						ToolInput = toolInput,
						CreatedAt = timestamp
					});
					break;
				}
			}
		}

		return messages;
	}

	private static IReadOnlyList<JobMessage> ExtractUserMessages(
		JsonElement root,
		DateTime timestamp,
		LiveTranscriptParserState parserState)
	{
		if (!root.TryGetProperty("message", out var messageProp))
		{
			return [];
		}

		if (!messageProp.TryGetProperty("content", out var contentProp)
			|| contentProp.ValueKind != JsonValueKind.Array)
		{
			return [];
		}

		var messages = new List<JobMessage>();

		foreach (var block in contentProp.EnumerateArray())
		{
			if (!block.TryGetProperty("type", out var blockType)
				|| !string.Equals(blockType.GetString(), "tool_result", StringComparison.Ordinal))
			{
				continue;
			}

			var toolUseId = block.TryGetProperty("tool_use_id", out var toolUseIdProp)
				? toolUseIdProp.GetString()
				: null;
			var output = block.TryGetProperty("content", out var toolOutputProp)
				? ExtractJsonContent(toolOutputProp)
				: string.Empty;

			messages.Add(new JobMessage
			{
				Role = MessageRole.ToolResult,
				Content = output,
				ToolName = parserState.ResolveToolName(toolUseId),
				ToolOutput = output,
				CreatedAt = timestamp
			});
		}

		return messages;
	}

	private static string ExtractJsonContent(JsonElement element)
	{
		return element.ValueKind switch
		{
			JsonValueKind.String => element.GetString() ?? string.Empty,
			JsonValueKind.Array => string.Join(Environment.NewLine, element.EnumerateArray()
				.Select(ExtractJsonContent)
				.Where(content => !string.IsNullOrWhiteSpace(content))),
			JsonValueKind.Object => element.GetRawText(),
			JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
			_ => element.GetRawText()
		};
	}

	private static bool IsCliWaitStatus(string content)
		=> content.StartsWith("[System] Still initializing...", StringComparison.OrdinalIgnoreCase)
			|| content.StartsWith("[System] Still waiting for response...", StringComparison.OrdinalIgnoreCase)
			|| content.StartsWith("[System] Still waiting (", StringComparison.OrdinalIgnoreCase);

	private sealed class LiveTranscriptParserState
	{
		private readonly Dictionary<string, string> _toolNamesById = new(StringComparer.Ordinal);
		private readonly Queue<string> _pendingToolNames = new();

		public void RegisterToolUse(string? toolId, string? toolName)
		{
			if (string.IsNullOrWhiteSpace(toolName))
			{
				return;
			}

			_pendingToolNames.Enqueue(toolName);

			if (!string.IsNullOrWhiteSpace(toolId))
			{
				_toolNamesById[toolId] = toolName;
			}
		}

		public string ResolveToolName(string? candidate)
		{
			if (!string.IsNullOrWhiteSpace(candidate) && _toolNamesById.TryGetValue(candidate, out var mappedToolName))
			{
				DequeueIfMatches(mappedToolName);
				return mappedToolName;
			}

			if (string.IsNullOrWhiteSpace(candidate) || LooksLikeGeneratedToolIdentifier(candidate))
			{
				if (_pendingToolNames.Count > 0)
				{
					return _pendingToolNames.Dequeue();
				}

				return string.IsNullOrWhiteSpace(candidate) ? "unknown_tool" : candidate;
			}

			DequeueIfMatches(candidate);
			return candidate;
		}

		private void DequeueIfMatches(string toolName)
		{
			if (_pendingToolNames.Count == 0)
			{
				return;
			}

			if (string.Equals(_pendingToolNames.Peek(), toolName, StringComparison.OrdinalIgnoreCase))
			{
				_pendingToolNames.Dequeue();
			}
		}
	}

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
