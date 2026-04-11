using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Models;

public class SkillImportRequest
{
	[Required]
	public string FileName { get; set; } = string.Empty;

	[Required]
	public byte[] Content { get; set; } = [];
}

public class SkillImportPreview
{
	public string Name { get; set; } = string.Empty;
	public string? Description { get; set; }
	public string Content { get; set; } = string.Empty;
	public bool IsEnabled { get; set; } = true;
	public bool NameExists { get; set; }
	public List<string> IncludedFiles { get; set; } = [];
	public List<string> Warnings { get; set; } = [];
}

public class SkillImportResult
{
	public bool Imported { get; set; }
	public bool Skipped { get; set; }
	public string Message { get; set; } = string.Empty;
	public Skill? Skill { get; set; }
	public SkillImportPreview? Preview { get; set; }
	public List<string> Warnings { get; set; } = [];
}
