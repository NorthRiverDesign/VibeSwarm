using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Models;

public sealed class CreateIdeaRequest
{
	[Required]
	public Guid ProjectId { get; set; }

	[Required]
	[StringLength(ValidationLimits.IdeaDescriptionMaxLength, MinimumLength = 1)]
	public string Description { get; set; } = string.Empty;

	public List<IdeaAttachmentUpload> Attachments { get; set; } = [];
}

public sealed class IdeaAttachmentUpload
{
	[Required]
	[StringLength(ValidationLimits.IdeaAttachmentFileNameMaxLength, MinimumLength = 1)]
	public string FileName { get; set; } = string.Empty;

	[StringLength(ValidationLimits.IdeaAttachmentContentTypeMaxLength)]
	public string? ContentType { get; set; }

	[Required]
	public byte[] Content { get; set; } = [];
}
