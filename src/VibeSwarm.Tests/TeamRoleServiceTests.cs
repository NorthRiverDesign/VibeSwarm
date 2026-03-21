using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class TeamRoleServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly DbContextOptions<VibeSwarmDbContext> _dbOptions;

	public TeamRoleServiceTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();

		_dbOptions = new DbContextOptionsBuilder<VibeSwarmDbContext>()
			.UseSqlite(_connection)
			.Options;

		using var dbContext = CreateDbContext();
		dbContext.Database.EnsureCreated();
	}

	[Fact]
	public async Task CreateAsync_PersistsSelectedSkills()
	{
		await using var dbContext = CreateDbContext();
		var skill = new Skill
		{
			Id = Guid.NewGuid(),
			Name = "frontend-design",
			Content = "Create polished UI."
		};
		dbContext.Skills.Add(skill);
		await dbContext.SaveChangesAsync();

		var service = new TeamRoleService(dbContext);
		var created = await service.CreateAsync(new TeamRole
		{
			Name = "Front End Designer",
			Description = "Shapes the UI.",
			Responsibilities = "Own layout and interaction quality.",
			SkillLinks =
			[
				new TeamRoleSkill
				{
					SkillId = skill.Id
				}
			]
		});

		Assert.Single(created.SkillLinks);
		Assert.Equal(skill.Id, created.SkillLinks.Single().SkillId);
		Assert.Equal("frontend-design", created.SkillLinks.Single().Skill?.Name);
	}

	[Fact]
	public async Task UpdateAsync_ReplacesSkillAssignments()
	{
		await using var dbContext = CreateDbContext();
		var firstSkill = new Skill
		{
			Id = Guid.NewGuid(),
			Name = "project-management",
			Content = "Plan and coordinate."
		};
		var secondSkill = new Skill
		{
			Id = Guid.NewGuid(),
			Name = "deployment",
			Content = "Ship safely."
		};
		dbContext.Skills.AddRange(firstSkill, secondSkill);
		await dbContext.SaveChangesAsync();

		var service = new TeamRoleService(dbContext);
		var created = await service.CreateAsync(new TeamRole
		{
			Name = "Project Manager",
			SkillLinks =
			[
				new TeamRoleSkill
				{
					SkillId = firstSkill.Id
				}
			]
		});

		created.SkillLinks =
		[
			new TeamRoleSkill
			{
				SkillId = secondSkill.Id
			}
		];
		var updated = await service.UpdateAsync(created);

		Assert.Single(updated.SkillLinks);
		Assert.Equal(secondSkill.Id, updated.SkillLinks.Single().SkillId);
		Assert.Equal("deployment", updated.SkillLinks.Single().Skill?.Name);
	}

	private VibeSwarmDbContext CreateDbContext()
	{
		return new VibeSwarmDbContext(_dbOptions);
	}

	public void Dispose()
	{
		_connection.Dispose();
	}
}
