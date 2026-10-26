using System.Globalization;
using System.Text.RegularExpressions;
namespace Wayfarer.Tests;

/// <summary>
/// Base images, P-1, P-2: every image a Dockerfile pulls is pinned by sha256 digest. Checked in every file named Dockerfile,
/// Dockerfile.*, Dockerfile-* or Dockerfile_* in the repository (P-2), for FROM, COPY --from=&lt;image&gt; and
/// RUN --mount=...,from=&lt;image&gt; (P-1). A reference to an earlier build stage (by name or index) and scratch are
/// not images and need no digest.
/// </summary>
public sealed class DockerfilePinningTests
{
    private const string ServiceDockerfile = "src/Wayfarer.Host/Dockerfile";

    private static readonly Regex DockerfileName = new(@"^Dockerfile([._-].+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PinnedImage = new(@"^[^\s@]+@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly string[] SkippedDirectories = [".git", "bin", "obj", "node_modules"];

    [Fact]
    public void Every_dockerfile_in_the_repository_pins_every_external_image_by_sha256_digest()
    {
        var root = FindRepoRoot();
        var files = FindDockerfiles(root);
        Assert.Contains(ServiceDockerfile, files.Select(f => Relative(root, f)));

        var problems = files
            .SelectMany(f => UnpinnedReferences(File.ReadAllText(f)).Select(p => $"{Relative(root, f)}: {p}"))
            .ToList();

        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("Dockerfile", true)]
    [InlineData("Dockerfile.prod", true)]
    [InlineData("Dockerfile-dev", true)]
    [InlineData("Dockerfile_ci", true)]
    [InlineData("dockerfile", true)]
    [InlineData("DockerfilePinningTests.cs", false)]
    [InlineData("Dockerfile.", false)]
    [InlineData(".dockerignore", false)]
    [InlineData("README.md", false)]
    public void Dockerfile_names_are_recognised(string name, bool expected)
    {
        Assert.Equal(expected, IsDockerfileName(name));
    }

    [Fact]
    public void Discovery_finds_every_dockerfile_variant_and_skips_build_output()
    {
        var root = Path.Combine(Path.GetTempPath(), "pin-scan-" + Guid.NewGuid().ToString("N"));
        try
        {
            string[] expected = ["Dockerfile", "Dockerfile.prod", "Dockerfile-dev", "Dockerfile_ci", "deploy/worker/Dockerfile.migrations"];
            string[] ignored = ["README.md", "DockerfileNotes.txt", "bin/Dockerfile", "obj/Dockerfile.ci", "node_modules/pkg/Dockerfile", ".git/Dockerfile"];
            foreach (var file in expected.Concat(ignored))
            {
                var full = Path.Combine(root, file);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "FROM scratch\n");
            }

            var found = FindDockerfiles(root).Select(f => Relative(root, f)).Order(StringComparer.Ordinal).ToArray();

            Assert.Equal(expected.Order(StringComparer.Ordinal).ToArray(), found);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private const string Digest = "@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee";

    public static TheoryData<string, string> UnpinnedDockerfiles => new()
    {
        { "FROM mcr.microsoft.com/dotnet/aspnet:10.0\n", "FROM mcr.microsoft.com/dotnet/aspnet:10.0" },
        { $"FROM base{Digest} AS build\nFROM base{Digest}\nCOPY --from=alpine:3.20 /etc/ssl /etc/ssl\n", "COPY --from=alpine:3.20" },
        { $"FROM base{Digest}\nCOPY --chown=app:app --from=\"busybox\" /bin/sh /sh\n", "COPY --from=busybox" },
        { $"FROM base{Digest}\ncopy \\\n  --from=alpine /a /b\n", "COPY --from=alpine" },
        { $"FROM base{Digest} AS final\nCOPY --from=later /a /b\nFROM base{Digest} AS later\n", "COPY --from=later" },
        { $"FROM base{Digest}\nCOPY --from=1 /a /b\nFROM base{Digest}\n", "COPY --from=1" },
        { $"FROM base{Digest}\nRUN --mount=type=bind,from=busybox:1.36,source=/bin,target=/x ls /x\n", "RUN --mount from=busybox:1.36" },
        { $"FROM base{Digest}\nRUN --mount=type=cache,target=/root/.nuget \\\n    --mount=from=alpine,target=/a \\\n    ls /a\n", "RUN --mount from=alpine" },
        { $"FROM base{Digest}\nRUN --mount=\"type=bind,from=alpine,target=/a\" ls\n", "RUN --mount from=alpine" },
    };

    [Theory]
    [MemberData(nameof(UnpinnedDockerfiles))]
    public void Unpinned_image_references_are_reported(string dockerfile, string expected)
    {
        var problems = UnpinnedReferences(dockerfile);

        Assert.Contains(problems, p => p.StartsWith(expected + " ", StringComparison.Ordinal));
    }

    [Fact]
    public void Pinned_images_earlier_stages_and_scratch_are_accepted()
    {
        var dockerfile = $"""
            # syntax and comments are ignored: COPY --from=alpine
            FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0{Digest} AS build
            RUN --mount=type=cache,target=/root/.nuget/packages dotnet restore
            FROM build AS test
            RUN --mount=type=bind,from=build,source=/src,target=/src ls /src
            FROM scratch AS empty
            FROM mcr.microsoft.com/dotnet/aspnet:10.0{Digest}
            COPY --from=build --chown=app:app /app/publish .
            COPY --from=0 /src/a /a
            COPY --from=busybox{Digest} /bin/busybox /busybox
            RUN --mount=type=bind,from=alpine{Digest},target=/a ls /a
            """;

        Assert.Empty(UnpinnedReferences(dockerfile));
    }

    internal static bool IsDockerfileName(string fileName) => DockerfileName.IsMatch(fileName);

    internal static List<string> FindDockerfiles(string root)
    {
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (!SkippedDirectories.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                {
                    pending.Push(sub);
                }
            }

            found.AddRange(Directory.EnumerateFiles(dir).Where(f => IsDockerfileName(Path.GetFileName(f))));
        }

        return found;
    }

    internal static List<string> UnpinnedReferences(string dockerfile)
    {
        var problems = new List<string>();
        var earlierStages = new List<string>();
        string? currentStage = null;
        var stageIndex = -1;

        foreach (var instruction in Instructions(dockerfile))
        {
            var tokens = instruction.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var keyword = tokens[0].ToUpperInvariant();
            var flags = tokens.Skip(1).TakeWhile(t => t.StartsWith("--", StringComparison.Ordinal)).ToList();

            switch (keyword)
            {
                case "FROM":
                    if (stageIndex >= 0 && currentStage is not null)
                    {
                        earlierStages.Add(currentStage);
                    }

                    stageIndex++;
                    var args = tokens.Skip(1 + flags.Count).ToList();
                    if (args.Count == 0)
                    {
                        problems.Add($"FROM without an image ({instruction})");
                        currentStage = null;
                        break;
                    }

                    var image = Unquote(args[0]);
                    if (!IsEarlierStage(image) && !image.Equals("scratch", StringComparison.OrdinalIgnoreCase) && !PinnedImage.IsMatch(image))
                    {
                        problems.Add($"FROM {image} is not pinned by digest");
                    }

                    currentStage = args.Count >= 3 && args[1].Equals("AS", StringComparison.OrdinalIgnoreCase) ? args[2] : null;
                    break;

                case "COPY":
                case "ADD":
                    foreach (var flag in flags.Where(f => f.StartsWith("--from=", StringComparison.OrdinalIgnoreCase)))
                    {
                        Check(Unquote(flag["--from=".Length..]), "COPY --from=");
                    }

                    break;

                case "RUN":
                    foreach (var flag in flags.Where(f => f.StartsWith("--mount=", StringComparison.OrdinalIgnoreCase)))
                    {
                        foreach (var option in Unquote(flag["--mount=".Length..]).Split(','))
                        {
                            if (option.StartsWith("from=", StringComparison.OrdinalIgnoreCase))
                            {
                                Check(Unquote(option["from=".Length..]), "RUN --mount from=");
                            }
                        }
                    }

                    break;
            }
        }

        return problems;

        void Check(string reference, string what)
        {
            if (!IsEarlierStage(reference) && !PinnedImage.IsMatch(reference))
            {
                problems.Add($"{what}{reference} is not an earlier build stage or an image pinned by digest");
            }
        }

        bool IsEarlierStage(string reference) =>
            earlierStages.Contains(reference, StringComparer.OrdinalIgnoreCase)
            || (int.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < stageIndex);
    }

    private static IEnumerable<string> Instructions(string dockerfile)
    {
        var current = new List<string>();
        foreach (var raw in dockerfile.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#'))
            {
                continue;
            }

            var continues = line.EndsWith('\\');
            if (continues)
            {
                line = line[..^1];
            }

            if (line.Length > 0)
            {
                current.Add(line);
            }

            if (!continues && current.Count > 0)
            {
                yield return string.Join(' ', current);
                current.Clear();
            }
        }

        if (current.Count > 0)
        {
            yield return string.Join(' ', current);
        }
    }

    private static string Unquote(string value) => value.Trim().Trim('"', '\'');

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root (.git).");
    }
}
