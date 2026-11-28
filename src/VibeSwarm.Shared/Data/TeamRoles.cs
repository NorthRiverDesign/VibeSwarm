namespace VibeSwarm.Shared.Data;

/// <summary>
/// The roles the team set in Settings fills on a project. The builder is not one of them: every
/// project keeps its own Build profile and fallbacks.
/// </summary>
[Flags]
public enum TeamRoles
{
	None = 0,

	/// <summary>Plans each job and suggests what to build next.</summary>
	Lead = 1,

	/// <summary>Checks each job's work and sends problems back.</summary>
	Review = 2
}
