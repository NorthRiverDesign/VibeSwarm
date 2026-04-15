using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Controllers;

namespace VibeSwarm.Tests;

public sealed class AgentsControllerTests
{
	[Fact]
	public async Task Create_WhenValidationFails_ReturnsBadRequestWithValidationMessage()
	{
		var controller = new AgentsController(new ThrowingAgentService(
			new ValidationException("Name is required.")));

		var result = await controller.Create(new Agent(), CancellationToken.None);

		var badRequest = Assert.IsType<BadRequestObjectResult>(result);
		Assert.Contains("Name is required.", SerializeValue(badRequest.Value));
	}

	[Fact]
	public async Task Create_WhenUniqueConstraintFails_ReturnsFriendlyDuplicateNameMessage()
	{
		var controller = new AgentsController(new ThrowingAgentService(
			CreateDbUpdateException("SQLite Error 19: 'UNIQUE constraint failed: Agents.Name'.")));

		var result = await controller.Create(new Agent { Name = "Security Reviewer" }, CancellationToken.None);

		var badRequest = Assert.IsType<BadRequestObjectResult>(result);
		Assert.Contains("An agent with this name already exists.", SerializeValue(badRequest.Value));
	}

	[Fact]
	public async Task Update_WhenSkillForeignKeyFails_ReturnsFriendlySkillValidationMessage()
	{
		var controller = new AgentsController(new ThrowingAgentService(
			CreateDbUpdateException("SQLite Error 19: 'FOREIGN KEY constraint failed: AgentSkills.SkillId -> Skills.Id'.")));

		var result = await controller.Update(Guid.NewGuid(), new Agent { Name = "Security Reviewer" }, CancellationToken.None);

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

	private sealed class ThrowingAgentService(Exception exception) : IAgentService
	{
		public Task<IEnumerable<Agent>> GetAllAsync(CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task<IEnumerable<Agent>> GetEnabledAsync(CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task<Agent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task<Agent> CreateAsync(Agent agent, CancellationToken cancellationToken = default)
			=> Task.FromException<Agent>(exception);

		public Task<Agent> UpdateAsync(Agent agent, CancellationToken cancellationToken = default)
			=> Task.FromException<Agent>(exception);

		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();
	}
}
