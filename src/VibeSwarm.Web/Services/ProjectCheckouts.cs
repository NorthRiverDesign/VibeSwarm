using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Projects that share a checkout: the same folder, or one inside another's. Every job resets and
/// cleans its project's folder before it starts, so jobs in projects that share one would wipe or
/// conflict with each other's work. <see cref="ProjectJobSlots"/> gives them one slot between them.
/// </summary>
internal sealed class ProjectCheckouts
{
	private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
		? StringComparison.OrdinalIgnoreCase
		: StringComparison.Ordinal;

	// Each project that shares its checkout, mapped to the project that stands for the group.
	private readonly Dictionary<Guid, Guid> _checkoutOf;
	private readonly ILookup<Guid, Guid> _projectsIn;

	private ProjectCheckouts(Dictionary<Guid, Guid> checkoutOf)
	{
		_checkoutOf = checkoutOf;
		_projectsIn = checkoutOf.ToLookup(entry => entry.Value, entry => entry.Key);
	}

	/// <summary>Every project in its own checkout.</summary>
	public static ProjectCheckouts None { get; } = new([]);

	public static async Task<ProjectCheckouts> LoadAsync(VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
	{
		var projects = await dbContext.Projects
			.AsNoTracking()
			.Select(project => new { project.Id, project.WorkingPath })
			.ToListAsync(cancellationToken);

		return From(projects.Select(project => (project.Id, (string?)project.WorkingPath)));
	}

	public static ProjectCheckouts From(IEnumerable<(Guid ProjectId, string? WorkingPath)> projects)
	{
		var folders = projects
			.Select(project => (project.ProjectId, Folder: NormalizeFolder(project.WorkingPath)))
			.Where(project => project.Folder != null)
			.OrderBy(project => project.Folder!.Length)
			.ThenBy(project => project.ProjectId)
			.ToList();

		var checkoutOf = new Dictionary<Guid, Guid>();
		for (var i = 0; i < folders.Count; i++)
		{
			for (var j = i + 1; j < folders.Count; j++)
			{
				if (Shares(folders[i].Folder!, folders[j].Folder!))
				{
					Join(checkoutOf, folders[i].ProjectId, folders[j].ProjectId);
				}
			}
		}

		// Point every member straight at its group's project.
		foreach (var projectId in checkoutOf.Keys.ToList())
		{
			checkoutOf[projectId] = Find(checkoutOf, projectId);
		}

		return new ProjectCheckouts(checkoutOf);
	}

	/// <summary>The project standing for the checkout this project works in (itself, unless it shares one).</summary>
	public Guid CheckoutOf(Guid projectId) => _checkoutOf.GetValueOrDefault(projectId, projectId);

	/// <summary>Every project working in the same checkout as this one, itself included.</summary>
	public IReadOnlyList<Guid> ProjectsSharing(Guid projectId)
	{
		var members = _projectsIn[CheckoutOf(projectId)].ToList();
		return members.Count == 0 ? [projectId] : members;
	}

	private static bool Shares(string shorter, string longer) =>
		string.Equals(shorter, longer, PathComparison)
		|| longer.StartsWith(shorter + Path.DirectorySeparatorChar, PathComparison);

	private static string? NormalizeFolder(string? workingPath)
	{
		if (string.IsNullOrWhiteSpace(workingPath))
		{
			return null;
		}

		try
		{
			return Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingPath.Trim()));
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return null;
		}
	}

	private static void Join(Dictionary<Guid, Guid> checkoutOf, Guid first, Guid second)
	{
		var firstRoot = Find(checkoutOf, first);
		var secondRoot = Find(checkoutOf, second);
		checkoutOf.TryAdd(firstRoot, firstRoot);
		checkoutOf[secondRoot] = firstRoot;
		checkoutOf.TryAdd(second, firstRoot);
	}

	private static Guid Find(Dictionary<Guid, Guid> checkoutOf, Guid projectId)
	{
		while (checkoutOf.TryGetValue(projectId, out var parent) && parent != projectId)
		{
			projectId = parent;
		}

		return projectId;
	}
}
