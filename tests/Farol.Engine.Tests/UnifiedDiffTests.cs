using Farol.Core.Text;
using Xunit;

namespace Farol.Engine.Tests;

public sealed class UnifiedDiffTests
{
    [Fact]
    public void Identical_texts_have_no_diff() =>
        Assert.Equal(string.Empty, UnifiedDiff.Create("a.cs", "x\ny\n", "x\ny\n"));

    [Fact]
    public void Inserted_line_comes_with_three_lines_of_context()
    {
        var diff = UnifiedDiff.Create("src/A.cs", "1\n2\n3\n4\n5\n6\n", "1\n2\n3\nnew\n4\n5\n6\n");

        Assert.Equal(
            """
            --- a/src/A.cs
            +++ b/src/A.cs
            @@ -1,6 +1,7 @@
             1
             2
             3
            +new
             4
             5
             6
            """.ReplaceLineEndings("\n"),
            diff);
    }

    [Fact]
    public void Distant_changes_get_separate_hunks()
    {
        var before = string.Join('\n', Enumerable.Range(1, 30)) + "\n";
        var after = before.Replace("\n2\n", "\ntwo\n", StringComparison.Ordinal).Replace("\n28\n", "\ntwenty-eight\n", StringComparison.Ordinal);

        var diff = UnifiedDiff.Create("a.cs", before, after);

        Assert.Contains("@@ -1,5 +1,5 @@", diff, StringComparison.Ordinal);
        Assert.Contains("@@ -25,6 +25,6 @@", diff, StringComparison.Ordinal);
        Assert.Contains("-28\n+twenty-eight", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void Created_and_deleted_files_diff_against_dev_null()
    {
        var created = UnifiedDiff.Create("New.cs", null, "a\nb\n");
        var deleted = UnifiedDiff.Create("Old.cs", "a\n", null);

        Assert.StartsWith("--- /dev/null\n+++ b/New.cs\n@@ -0,0 +1,2 @@\n+a\n+b", created, StringComparison.Ordinal);
        Assert.StartsWith("--- a/Old.cs\n+++ /dev/null\n@@ -1,1 +0,0 @@\n-a", deleted, StringComparison.Ordinal);
    }

    [Fact]
    public void Crlf_input_produces_the_same_lines_and_a_missing_final_newline_is_marked()
    {
        var diff = UnifiedDiff.Create("a.vb", "Line1\r\nLine2", "Line1\r\nLine2\r\nLine3");

        Assert.Equal(
            "--- a/a.vb\n+++ b/a.vb\n@@ -1,2 +1,3 @@\n Line1\n-Line2\n\\ No newline at end of file\n+Line2\n+Line3\n\\ No newline at end of file",
            diff);
    }
}
