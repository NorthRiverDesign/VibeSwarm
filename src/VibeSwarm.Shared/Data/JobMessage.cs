using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VibeSwarm.Shared.Data;

public class JobMessage
{
    public Guid Id { get; set; }

    public Guid JobId { get; set; }
    public Job? Job { get; set; }

    [Required]
    public MessageRole Role { get; set; }

    [Required]
    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// For tool use messages, the name of the tool
    /// </summary>
    public string? ToolName { get; set; }

    /// <summary>
    /// For tool use messages, the tool input (JSON)
    /// </summary>
    public string? ToolInput { get; set; }

    /// <summary>
    /// For tool result messages, the tool output
    /// </summary>
    public string? ToolOutput { get; set; }

    /// <summary>
    /// Token count for this message (if available)
    /// </summary>
    public int? TokenCount { get; set; }

    /// <summary>
    /// Where this message originated from. Not persisted — assigned at display time.
    /// </summary>
    [NotMapped]
    public MessageSource Source { get; set; } = MessageSource.System;

    /// <summary>
    /// Severity level for styling. Not persisted — assigned at display time.
    /// </summary>
    [NotMapped]
    public MessageLevel Level { get; set; } = MessageLevel.Normal;
}

public enum MessageRole
{
    User,
    Assistant,
    System,
    ToolUse,
    ToolResult
}

/// <summary>
/// Where a system message originated from.
/// </summary>
public enum MessageSource
{
    /// <summary>The host application (orchestration, status updates).</summary>
    System,
    /// <summary>A CLI coding provider (Claude, Copilot, etc.).</summary>
    Provider,
    /// <summary>User-initiated action.</summary>
    User
}

/// <summary>
/// Severity level for a system message.
/// </summary>
public enum MessageLevel
{
    Normal,
    Warning,
    Error,
    Success
}
