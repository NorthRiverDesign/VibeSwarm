using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Controllers;

namespace VibeSwarm.Tests;

public sealed class TeamRolesControllerTests
{
	[Fact]
	public async Task Create_WhenValidationFails_ReturnsBadRequestWithValidationMessage()
	{
		var controller = new TeamRolesController(new ThrowingTeamRoleService(
			new ValidationException("Name is required.")));

		var result = await controller.Create(new TeamRole(), CancellationToken.None);

		var badRequest = Assert.IsType<BadRequestObjectResult>(result);
		Assert.Contains("Name is required.", SerializeValue(badRequest.Value));
	}

	[Fact]
	public async Task Create_WhenUniqueConstraintFails_ReturnsFriendlyDuplicateNameMessage()
	{
		var controller = new TeamRolesController(new ThrowingTeamRoleService(
			CreateDbUpdateException("SQLite Error 19: 'UNIQUE constraint failed: TeamRoles.Name'.")));

		var result = await controller.Create(new TeamRole { Name = "Security Reviewer" }, CancellationToken.None);

		var badRequest = Assert.IsType<BadRequestObjectResult>(result);
		Assert.Contains("A team role with this name already exists.", SerializeValue(badRequest.Value));
	}

	[Fact]
	public async Task Update_WhenSkillForeignKeyFails_ReturnsFriendlySkillValidationMessage()
	{
		var controller = new TeamRolesController(new ThrowingTeamRoleService(
			CreateDbUpdateException("SQLite Error 19: 'FOREIGN KEY constraint failed: TeamRoleSkills.SkillId -> Skills.Id'.")));

		var result = await controller.Update(Guid.NewGuid(), new TeamRole { Name = "Security Reviewer" }, CancellationToken.None);

		var badRequest = Assert.IsType<BadRequestObjectResult>(result);
		Assert.Contains("One or more selected skills do not exist.", SerializeValue(badRequest.Value));
	}

	private static DbUpdateException CreateDbUpdateException(string message)
	{
		return new DbUpdateException("An error occurred while saving the entity changes. See the inner exception for details.", new Exception(message));
	}

	private static string SerializeValue(object? value)
	{
		return JsonSerializer.Serialize(value);
	}

	private sealed class ThrowingTeamRoleService(Exception exception) : ITeamRoleService
	{
		public Task<IEnumerable<TeamRole>> GetAllAsync(CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task<IEnumerable<TeamRole>> GetEnabledAsync(CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task<TeamRole?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task<TeamRole> CreateAsync(TeamRole teamRole, CancellationToken cancellationToken = default)
			=> Task.FromException<TeamRole>(exception);

		public Task<TeamRole> UpdateAsync(TeamRole teamRole, CancellationToken cancellationToken = default)
			=> Task.FromException<TeamRole>(exception);

		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();
	}
}
