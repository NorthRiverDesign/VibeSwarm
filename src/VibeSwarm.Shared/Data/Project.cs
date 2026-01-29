using System.ComponentModel.DataAnnotations;

namespace VibeSwarm.Shared.Data;

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

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    public ICollection<Job> Jobs { get; set; } = new List<Job>();

    public ICollection<Idea> Ideas { get; set; } = new List<Idea>();

    /// <summary>
    /// Whether Ideas auto-processing is currently running for this project
    /// </summary>
    public bool IdeasProcessingActive { get; set; }
}
