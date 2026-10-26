using System.Text.RegularExpressions;

namespace Wayfarer.Tests;

/// <summary>Base images: every base image in the service Dockerfile is pinned by digest.</summary>
public sealed class DockerfilePinningTests
{
    [Fact]
    public void Every_from_line_pins_the_base_image_by_sha256_digest()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "Wayfarer.Host", "Dockerfile");
        var from = File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("FROM ", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(from);
        Assert.All(from, line => Assert.Matches(new Regex(@"^FROM \S+@sha256:[0-9a-f]{64}(\s+AS\s+\S+)?$", RegexOptions.IgnoreCase), line));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Wayfarer.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate Wayfarer.sln.");
    }
}
