using System.ComponentModel.DataAnnotations;

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
}

public enum MessageRole
{
    User,
    Assistant,
    System,
    ToolUse,
    ToolResult
}
