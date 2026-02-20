using System.ComponentModel.DataAnnotations;

namespace VibeSwarm.Shared.Data;

/// <summary>
/// Specifies the auto-commit behavior after a job completes successfully.
/// </summary>
public enum AutoCommitMode
{
    /// <summary>
    /// No automatic commit. Changes must be committed manually.
    /// </summary>
    Off = 0,

    /// <summary>
    /// Automatically commit changes after job completion, but do not push.
    /// </summary>
    CommitOnly = 1,

    /// <summary>
    /// Automatically commit and push changes after job completion.
    /// </summary>
    CommitAndPush = 2
}

public class Project
{
    public Guid Id { get; set; }

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    [Required]
    [StringLength(500, MinimumLength = 1)]
    public string WorkingPath { get; set; } = string.Empty;

    /// <summary>
    /// Optional GitHub repository in "owner/repo" format.
    /// Used when creating a project from a GitHub repository.
    /// </summary>
    [StringLength(200)]
    public string? GitHubRepository { get; set; }

    /// <summary>
    /// Auto-commit behavior after job completion.
    /// </summary>
    public AutoCommitMode AutoCommitMode { get; set; } = AutoCommitMode.Off;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    public ICollection<Job> Jobs { get; set; } = new List<Job>();

    public ICollection<Idea> Ideas { get; set; } = new List<Idea>();

    /// <summary>
    /// Optional per-project instructions injected into every job prompt.
    /// E.g., coding conventions, framework preferences, language requirements.
    /// </summary>
    [StringLength(1000)]
    public string? PromptContext { get; set; }

    /// <summary>
    /// Cached repo map (compact file tree) generated from the project directory.
    /// Injected into the system prompt to give agents a head start on project structure.
    /// </summary>
    public string? RepoMap { get; set; }

    /// <summary>
    /// When the repo map was last generated. Used to determine staleness (regenerate after 24h).
    /// </summary>
    public DateTime? RepoMapGeneratedAt { get; set; }

    /// <summary>
    /// Whether the project is active. Inactive projects are hidden from the dashboard
    /// and shown with reduced emphasis in other views.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether Ideas auto-processing is currently running for this project
    /// </summary>
    public bool IdeasProcessingActive { get; set; }

    /// <summary>
    /// Whether to auto-commit changes when jobs from ideas complete during auto-processing
    /// </summary>
    public bool IdeasAutoCommit { get; set; }
}
