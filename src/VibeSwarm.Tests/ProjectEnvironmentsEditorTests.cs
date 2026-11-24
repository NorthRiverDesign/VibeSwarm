using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using VibeSwarm.Client.Components.Projects;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Tests;

public sealed class ProjectEnvironmentsEditorTests
{
	[Fact]
	public void DatabaseEnvironment_OffersALogin_AndNamesItsJobVariables()
	{
		using var context = new BunitContext();
		var environment = NewEnvironment("Prod DB", EnvironmentType.Database, "mysql://db.example.com:3306/app");

		var cut = RenderEditor(context, environment);
		cut.Find("button[aria-expanded]").Click();

		Assert.Single(cut.FindAll($"#env-username-{environment.Id}"));
		Assert.Single(cut.FindAll($"#env-password-{environment.Id}"));
		Assert.Contains("Jobs get its URL and login as APP_PROD_DB_* variables. The prompt names them but leaves out the login itself.", cut.Markup);
	}

	[Fact]
	public void ReleaseEnvironment_HasNoLogin()
	{
		using var context = new BunitContext();
		var environment = NewEnvironment("Releases", EnvironmentType.Release, "https://github.com/octo/repo/releases");

		var cut = RenderEditor(context, environment);
		cut.Find("button[aria-expanded]").Click();

		Assert.Empty(cut.FindAll($"#env-username-{environment.Id}"));
		Assert.Contains("Jobs get its URL as APP_RELEASES_URL.", cut.Markup);
	}

	private static ProjectEnvironment NewEnvironment(string name, EnvironmentType type, string url) => new()
	{
		Id = Guid.NewGuid(),
		Name = name,
		Type = type,
		Stage = EnvironmentStage.Production,
		Url = url,
		IsEnabled = true,
		IsPrimary = true
	};

	private static IRenderedComponent<EditForm> RenderEditor(BunitContext context, ProjectEnvironment environment)
	{
		var project = new Project { Name = "Shop", Environments = [environment] };
		return context.Render<EditForm>(parameters => parameters
			.Add(form => form.Model, project)
			.Add<ProjectEnvironmentsEditor, EditContext>(form => form.ChildContent, _ => editor => editor.Add(e => e.Project, project)));
	}
}
