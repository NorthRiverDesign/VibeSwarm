using System.ComponentModel.DataAnnotations;
using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Shared.Services;

public class SkillService : ISkillService
{
	private const int SkillDescriptionMaxLength = 500;
	private readonly VibeSwarmDbContext _dbContext;
	private readonly IProviderService _providerService;
	private readonly ILogger<SkillService> _logger;

	public SkillService(VibeSwarmDbContext dbContext, IProviderService providerService, ILogger<SkillService> logger)
	{
		_dbContext = dbContext;
		_providerService = providerService;
		_logger = logger;
	}

	public async Task<IEnumerable<Skill>> GetAllAsync(CancellationToken cancellationToken = default)
	{
		return await _dbContext.Skills
			.OrderBy(s => s.Name)
			.ToListAsync(cancellationToken);
	}

	public async Task<IEnumerable<Skill>> GetEnabledAsync(CancellationToken cancellationToken = default)
	{
		return await _dbContext.Skills
			.Where(s => s.IsEnabled)
			.OrderBy(s => s.Name)
			.ToListAsync(cancellationToken);
	}

	public async Task<Skill?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
	{
		return await _dbContext.Skills
			.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
	}

	public async Task<Skill?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
	{
		return await _dbContext.Skills
			.FirstOrDefaultAsync(s => s.Name == name, cancellationToken);
	}

	public async Task<Skill> CreateAsync(Skill skill, CancellationToken cancellationToken = default)
	{
		NormalizeSkill(skill);
		ValidationHelper.ValidateObject(skill);
		await EnsureUniqueNameAsync(skill.Name, excludeId: null, cancellationToken);

		skill.Id = Guid.NewGuid();
		skill.CreatedAt = DateTime.UtcNow;

		_dbContext.Skills.Add(skill);
		await _dbContext.SaveChangesAsync(cancellationToken);

		return skill;
	}

	public async Task<Skill> UpdateAsync(Skill skill, CancellationToken cancellationToken = default)
	{
		NormalizeSkill(skill);
		ValidationHelper.ValidateObject(skill);
		await EnsureUniqueNameAsync(skill.Name, skill.Id, cancellationToken);

		var existing = await _dbContext.Skills
			.FirstOrDefaultAsync(s => s.Id == skill.Id, cancellationToken);

		if (existing == null)
		{
			throw new InvalidOperationException($"Skill with ID {skill.Id} not found.");
		}

		existing.Name = skill.Name;
		existing.Description = skill.Description;
		existing.Content = skill.Content;
		existing.IsEnabled = skill.IsEnabled;
		existing.UpdatedAt = DateTime.UtcNow;

		await _dbContext.SaveChangesAsync(cancellationToken);

		return existing;
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
	{
		var skill = await _dbContext.Skills
			.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

		if (skill != null)
		{
			_dbContext.Skills.Remove(skill);
			await _dbContext.SaveChangesAsync(cancellationToken);
		}
	}

	public async Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default)
	{
		var query = _dbContext.Skills.Where(s => s.Name == name);

		if (excludeId.HasValue)
		{
			query = query.Where(s => s.Id != excludeId.Value);
		}

		return await query.AnyAsync(cancellationToken);
	}

	public async Task<SkillImportPreview> PreviewImportAsync(SkillImportRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		var imported = ParseSkillImport(request);
		var preview = imported.Preview;
		preview.NameExists = await NameExistsAsync(preview.Name, cancellationToken: cancellationToken);
		return preview;
	}

	public async Task<SkillImportResult> ImportAsync(SkillImportRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		var imported = ParseSkillImport(request);
		var preview = imported.Preview;
		var existing = await _dbContext.Skills.FirstOrDefaultAsync(s => s.Name == preview.Name, cancellationToken);

		if (existing != null)
		{
			preview.NameExists = true;
			return new SkillImportResult
			{
				Skipped = true,
				Message = $"Skill '{preview.Name}' already exists.",
				Skill = existing,
				Preview = preview,
				Warnings = [..preview.Warnings]
			};
		}

		var skill = new Skill
		{
			Name = preview.Name,
			Description = preview.Description,
			Content = preview.Content,
			IsEnabled = preview.IsEnabled
		};

		var created = await CreateAsync(skill, cancellationToken);
		preview.NameExists = false;

		return new SkillImportResult
		{
			Imported = true,
			Message = $"Imported skill '{created.Name}'.",
			Skill = created,
			Preview = preview,
			Warnings = [..preview.Warnings]
		};
	}

	public async Task<string?> ExpandSkillAsync(string description, Guid providerId, string? modelId = null, CancellationToken cancellationToken = default)
	{
		var provider = await _providerService.GetByIdAsync(providerId, cancellationToken);
		if (provider == null)
		{
			_logger.LogWarning("Provider {ProviderId} not found for skill expansion", providerId);
			return null;
		}

		var providerInstance = _providerService.CreateInstance(provider);
		if (providerInstance == null)
		{
			_logger.LogWarning("Could not create provider instance for {ProviderName}", provider.Name);
			return null;
		}

		var expansionPrompt = BuildSkillExpansionPrompt(description);

		try
		{
			var response = await providerInstance.GetPromptResponseAsync(
				expansionPrompt,
				workingDirectory: null, // Skills don't have a specific project context
				cancellationToken);

			if (response.Success && !string.IsNullOrWhiteSpace(response.Response))
			{
				_logger.LogInformation("Successfully expanded skill description");
				return response.Response.Trim();
			}

			_logger.LogWarning("Failed to expand skill: {Error}", response.ErrorMessage ?? "No response");
			return null;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error expanding skill description");
			return null;
		}
	}

	private static string BuildSkillExpansionPrompt(string description)
	{
		return $@"You are creating a detailed skill definition for an AI coding agent. A skill is a set of instructions that guides the AI agent on how to perform specific tasks.

## Skill Description
{description}

## Task
Generate comprehensive Markdown content for this skill that includes:

1. **Purpose**: A clear explanation of what this skill enables the AI to do
2. **When to Use**: Specific situations or triggers for applying this skill
3. **Instructions**: Step-by-step guidance for the AI to follow
4. **Best Practices**: Tips and recommendations for optimal results
5. **Examples**: If applicable, show sample inputs/outputs or usage scenarios
6. **Constraints**: Any limitations or things to avoid

Write the content in clear, instructional Markdown format that an AI agent can easily follow. Be specific and actionable.";
	}

	private static void NormalizeSkill(Skill skill)
	{
		skill.Name = skill.Name.Trim();
		skill.Description = string.IsNullOrWhiteSpace(skill.Description)
			? null
			: skill.Description.Trim();
		skill.Content = skill.Content.Trim();
	}

	private async Task EnsureUniqueNameAsync(string name, Guid? excludeId, CancellationToken cancellationToken)
	{
		if (await NameExistsAsync(name, excludeId, cancellationToken))
		{
			throw new InvalidOperationException($"A skill named '{name}' already exists.");
		}
	}

	private static ParsedSkillImport ParseSkillImport(SkillImportRequest request)
	{
		if (string.IsNullOrWhiteSpace(request.FileName))
		{
			throw new ValidationException("File name is required.");
		}

		if (request.Content.Length == 0)
		{
			throw new ValidationException("Skill file content is empty.");
		}

		if (!request.FileName.EndsWith(".skill", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("Only Claude-exported .skill files are supported.");
		}

		using var archiveStream = new MemoryStream(request.Content, writable: false);
		using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);

		var skillEntry = archive.Entries
			.FirstOrDefault(entry => entry.FullName.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase));
		if (skillEntry == null)
		{
			throw new InvalidDataException("The .skill archive is missing SKILL.md.");
		}

		var warnings = new List<string>();
		var includedFiles = new List<string> { skillEntry.FullName };
		var skillMarkdown = ReadEntryAsString(skillEntry);
		if (string.IsNullOrWhiteSpace(skillMarkdown))
		{
			throw new InvalidDataException("The .skill archive contains an empty SKILL.md file.");
		}

		var (metadata, body) = ParseFrontMatter(skillMarkdown);
		var fallbackName = Path.GetFileNameWithoutExtension(request.FileName);
		var name = NormalizeImportedName(GetMetadataValue(metadata, "name"), fallbackName);
		var description = NormalizeDescription(GetMetadataValue(metadata, "description"), warnings);

		var contentBuilder = new StringBuilder(body.Trim());
		var referenceEntries = archive.Entries
			.Where(entry =>
				!string.IsNullOrEmpty(entry.Name) &&
				(
					entry.FullName.StartsWith("bootstrap-ui/references/", StringComparison.OrdinalIgnoreCase) ||
				entry.FullName.Contains("/references/", StringComparison.OrdinalIgnoreCase))
				)
			.OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
			.ToList();

		foreach (var referenceEntry in referenceEntries)
		{
			var referenceContent = ReadEntryAsString(referenceEntry).Trim();
			if (string.IsNullOrWhiteSpace(referenceContent))
			{
				continue;
			}

			includedFiles.Add(referenceEntry.FullName);
			contentBuilder
				.AppendLine()
				.AppendLine()
				.AppendLine("---")
				.AppendLine()
				.AppendLine($"## Reference: {Path.GetFileNameWithoutExtension(referenceEntry.Name)}")
				.AppendLine()
				.AppendLine(referenceContent);
		}

		var content = contentBuilder.ToString().Trim();
		if (string.IsNullOrWhiteSpace(content))
		{
			throw new ValidationException("Imported skill content is empty.");
		}

		return new ParsedSkillImport(new SkillImportPreview
		{
			Name = name,
			Description = description,
			Content = content,
			IsEnabled = true,
			IncludedFiles = includedFiles,
			Warnings = warnings
		});
	}

	private static string ReadEntryAsString(ZipArchiveEntry entry)
	{
		using var stream = entry.Open();
		using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		return reader.ReadToEnd();
	}

	private static (Dictionary<string, string> metadata, string body) ParseFrontMatter(string content)
	{
		var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
		if (lines.Length == 0 || lines[0].Trim() != "---")
		{
			return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), content);
		}

		var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var index = 1;
		while (index < lines.Length)
		{
			var line = lines[index];
			if (line.Trim() == "---")
			{
				index++;
				break;
			}

			if (string.IsNullOrWhiteSpace(line))
			{
				index++;
				continue;
			}

			var separatorIndex = line.IndexOf(':');
			if (separatorIndex <= 0)
			{
				index++;
				continue;
			}

			var key = line[..separatorIndex].Trim();
			var value = line[(separatorIndex + 1)..].Trim();

			if (value is ">" or "|" or ">-" or "|-")
			{
				index++;
				var continuation = new List<string>();
				while (index < lines.Length)
				{
					var continuationLine = lines[index];
					if (continuationLine.Trim() == "---")
					{
						break;
					}

					if (continuationLine.Length > 0 && char.IsWhiteSpace(continuationLine[0]))
					{
						continuation.Add(continuationLine.Trim());
						index++;
						continue;
					}

					break;
				}

				metadata[key] = string.Join(' ', continuation).Trim();
				continue;
			}

			metadata[key] = value.Trim().Trim('"');
			index++;
		}

		var body = string.Join('\n', lines.Skip(index)).Trim();
		return (metadata, body);
	}

	private static string? GetMetadataValue(IReadOnlyDictionary<string, string> metadata, string key)
		=> metadata.TryGetValue(key, out var value) ? value : null;

	private static string NormalizeImportedName(string? rawName, string fallbackName)
	{
		var source = string.IsNullOrWhiteSpace(rawName) ? fallbackName : rawName;
		var normalized = new string(source
			.Trim()
			.ToLowerInvariant()
			.Select(character => char.IsLetterOrDigit(character) ? character : '-')
			.ToArray());

		while (normalized.Contains("--", StringComparison.Ordinal))
		{
			normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
		}

		normalized = normalized.Trim('-');
		if (string.IsNullOrWhiteSpace(normalized))
		{
			throw new ValidationException("Imported skill name is empty.");
		}

		return normalized.Length <= 100
			? normalized
			: normalized[..100].TrimEnd('-');
	}

	private static string? NormalizeDescription(string? description, List<string> warnings)
	{
		if (string.IsNullOrWhiteSpace(description))
		{
			return null;
		}

		var trimmed = description.Trim();
		if (trimmed.Length <= SkillDescriptionMaxLength)
		{
			return trimmed;
		}

		warnings.Add($"Description was truncated to {SkillDescriptionMaxLength} characters to fit VibeSwarm limits.");
		return trimmed[..SkillDescriptionMaxLength].TrimEnd();
	}

	private sealed record ParsedSkillImport(SkillImportPreview Preview);
}
