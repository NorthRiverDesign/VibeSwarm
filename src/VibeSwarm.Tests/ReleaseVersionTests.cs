using VibeSwarm.Web.Services.Updates;

namespace VibeSwarm.Tests;

public class ReleaseVersionTests
{
	[Theory]
	[InlineData("v1.2.3", 1, 2, 3, null)]
	[InlineData("1.2.3", 1, 2, 3, null)]
	[InlineData("1.2", 1, 2, 0, null)]
	[InlineData("v2.0.0-beta.1", 2, 0, 0, "beta.1")]
	[InlineData("1.4.0+3f2a9c1", 1, 4, 0, null)]
	[InlineData("0.0.0-dev+abc", 0, 0, 0, "dev")]
	public void TryParse_ReadsTagsAndStampedVersions(string text, int major, int minor, int patch, string? prerelease)
	{
		Assert.True(ReleaseVersion.TryParse(text, out var version));
		Assert.Equal(new ReleaseVersion(major, minor, patch, prerelease), version);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("latest")]
	[InlineData("1")]
	[InlineData("1.2.3.4")]
	[InlineData("1.2.x")]
	[InlineData("1.2.3-")]
	[InlineData("1.2.3-beta/1")]
	public void TryParse_RejectsWhatIsNotAVersion(string? text)
	{
		Assert.False(ReleaseVersion.TryParse(text, out _));
	}

	[Theory]
	[InlineData("1.2.4", "1.2.3")]
	[InlineData("1.10.0", "1.9.9")]
	[InlineData("2.0.0", "2.0.0-rc.1")]
	[InlineData("2.0.0-rc.1", "2.0.0-beta.9")]
	[InlineData("2.0.0-beta.10", "2.0.0-beta.9")]
	[InlineData("2.0.0-beta.1", "2.0.0-beta")]
	[InlineData("2.0.0-beta", "2.0.0-1")]
	public void CompareTo_FollowsSemVerPrecedence(string newer, string older)
	{
		ReleaseVersion.TryParse(newer, out var newerVersion);
		ReleaseVersion.TryParse(older, out var olderVersion);

		Assert.True(newerVersion! > olderVersion!);
		Assert.True(olderVersion! < newerVersion!);
	}

	[Fact]
	public void DevelopmentBuild_IsRecognised()
	{
		ReleaseVersion.TryParse("0.0.0-dev+3f2a9c1", out var dev);
		ReleaseVersion.TryParse("0.0.1", out var release);

		Assert.True(dev!.IsDevelopmentBuild);
		Assert.False(release!.IsDevelopmentBuild);
	}
}
