using VibeSwarm.Shared.VersionControl;

namespace VibeSwarm.Tests;

public sealed class GitDiffParserTests
{
	[Fact]
	public void ParseDiff_ReturnsDiffFiles_ForModifiedAndNewFiles()
	{
		const string diff = """
diff --git a/src/Existing.cs b/src/Existing.cs
index 1111111..2222222 100644
--- a/src/Existing.cs
+++ b/src/Existing.cs
@@ -1,2 +1,3 @@
 public class Existing
 {
+	public int Value { get; set; }
 }
diff --git a/src/NewFile.cs b/src/NewFile.cs
new file mode 100644
--- /dev/null
+++ b/src/NewFile.cs
@@ -0,0 +1,2 @@
+public class NewFile
+{
""";

		var files = GitDiffParser.ParseDiff(diff);

		Assert.Equal(2, files.Count);

		Assert.Equal("src/Existing.cs", files[0].FileName);
		Assert.False(files[0].IsNew);
		Assert.False(files[0].IsDeleted);
		Assert.Equal(1, files[0].Additions);
		Assert.Equal(0, files[0].Deletions);
		Assert.Contains("diff --git a/src/Existing.cs b/src/Existing.cs", files[0].DiffContent);

		Assert.Equal("src/NewFile.cs", files[1].FileName);
		Assert.True(files[1].IsNew);
		Assert.Equal(2, files[1].Additions);
		Assert.Equal(0, files[1].Deletions);
	}

	[Fact]
	public void ParseDiff_MarksDeletedFiles_AndCountsRemovedLines()
	{
		const string diff = """
diff --git a/src/Removed.cs b/src/Removed.cs
deleted file mode 100644
--- a/src/Removed.cs
+++ /dev/null
@@ -1,2 +0,0 @@
-public class Removed
-{
""";

		var files = GitDiffParser.ParseDiff(diff);

		var file = Assert.Single(files);
		Assert.Equal("src/Removed.cs", file.FileName);
		Assert.True(file.IsDeleted);
		Assert.False(file.IsNew);
		Assert.Equal(0, file.Additions);
		Assert.Equal(2, file.Deletions);
	}

	[Fact]
	public void ParseDiff_IgnoresCommandNoise_AndKeepsOnlyDiffLines()
	{
		const string diff = """
git diff HEAD --stat --patch --find-renames --find-copies
 src/Noisy.cs | 2 +-
 1 file changed, 1 insertion(+), 1 deletion(-)
diff --git a/src/Noisy.cs b/src/Noisy.cs
index 1111111..2222222 100644
--- a/src/Noisy.cs
+++ b/src/Noisy.cs
@@ -1,2 +1,2 @@
-old value
+new value
$ git status --short
 M src/Noisy.cs
""";

		var files = GitDiffParser.ParseDiff(diff);

		var file = Assert.Single(files);
		Assert.Equal("src/Noisy.cs", file.FileName);
		Assert.Equal(1, file.Additions);
		Assert.Equal(1, file.Deletions);
		Assert.DoesNotContain("git diff HEAD", file.DiffContent, StringComparison.Ordinal);
		Assert.DoesNotContain("$ git status --short", file.DiffContent, StringComparison.Ordinal);
		Assert.DoesNotContain("1 file changed", file.DiffContent, StringComparison.Ordinal);
		Assert.Contains("@@ -1,2 +1,2 @@", file.DiffContent);
		Assert.Contains("+new value", file.DiffContent);
	}

	[Fact]
	public void FormatDiffHtml_IgnoresCommandNoise_AndOnlyRendersRelevantChanges()
	{
		const string diff = """
git diff HEAD --stat --patch
diff --git a/src/Noisy.cs b/src/Noisy.cs
index 1111111..2222222 100644
--- a/src/Noisy.cs
+++ b/src/Noisy.cs
@@ -1 +1 @@
-old value
+new value
Command completed successfully.
""";

		var html = GitDiffParser.FormatDiffHtml(diff);

		Assert.DoesNotContain("git diff HEAD", html, StringComparison.Ordinal);
		Assert.DoesNotContain("Command completed successfully.", html, StringComparison.Ordinal);
		Assert.Contains("old value", html);
		Assert.Contains("new value", html);
		Assert.Contains("diff-hunk", html);
	}
}
