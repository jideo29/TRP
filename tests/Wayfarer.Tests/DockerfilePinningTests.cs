using System.Globalization;
using System.Text.RegularExpressions;
namespace Wayfarer.Tests;

/// <summary>
/// Base images, P-1, P-2, P-3: every image a Dockerfile pulls is pinned by sha256 digest. Checked in every file named Dockerfile,
/// Dockerfile.*, Dockerfile-* or Dockerfile_* (P-2), *.Dockerfile, Containerfile, Containerfile.*, Containerfile-*,
/// Containerfile_* or *.Containerfile (P-3) in the repository, for FROM, COPY --from=&lt;image&gt; and
/// RUN --mount=...,from=&lt;image&gt; (P-1). A reference to an earlier build stage (by name or index) and scratch are
/// not images and need no digest. P-3: the <c># escape=</c> parser directive at the top of the file sets the line
/// continuation character (so a backtick continuation cannot hide COPY --from), and ONBUILD instructions are checked
/// too. An ONBUILD trigger runs in a later build whose stages this file cannot see, so its --from must be a digest.
/// </summary>
public sealed class DockerfilePinningTests
{
    private const string ServiceDockerfile = "src/Wayfarer.Host/Dockerfile";

    private static readonly Regex DockerfileName = new(@"^(?:(?:Dockerfile|Containerfile)(?:[._-].+)?|.+\.(?:Dockerfile|Containerfile))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
    [InlineData("api.Dockerfile", true)]
    [InlineData("worker.dockerfile", true)]
    [InlineData("Containerfile", true)]
    [InlineData("Containerfile.dev", true)]
    [InlineData("Containerfile-ci", true)]
    [InlineData("api.Containerfile", true)]
    [InlineData(".Dockerfile", false)]
    [InlineData("notes.Dockerfile.md", false)]
    [InlineData("ContainerfileNotes.txt", false)]
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
            string[] expected = ["Dockerfile", "Dockerfile.prod", "Dockerfile-dev", "Dockerfile_ci", "deploy/worker/Dockerfile.migrations", "api.Dockerfile", "Containerfile", "deploy/Containerfile.worker", "tools/web.containerfile"];
            string[] ignored = ["README.md", "DockerfileNotes.txt", "bin/Dockerfile", "obj/Dockerfile.ci", "node_modules/pkg/Dockerfile", ".git/Dockerfile", "notes.Dockerfile.md", "bin/api.Dockerfile", "obj/Containerfile"];
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
        { $"# escape=`\nFROM base{Digest}\nCOPY `\n  --from=alpine:3.20 /a /b\n", "COPY --from=alpine:3.20" },
        { $"#  ESCAPE = `\n# syntax=docker/dockerfile:1\nFROM base{Digest}\nRUN `\n  --mount=type=bind,from=busybox,target=/x ls\n", "RUN --mount from=busybox" },
        { $"# syntax=docker/dockerfile:1\n# escape=`\nFROM base{Digest}\nCOPY `\n  --from=alpine /a /b\n", "COPY --from=alpine" },
        { $"# note\n# escape=`\nFROM base{Digest}\nCOPY \\\n  --from=alpine /a /b\n", "COPY --from=alpine" },
        { $"FROM base{Digest}\nONBUILD COPY --from=alpine:3.20 /a /b\n", "ONBUILD COPY --from=alpine:3.20" },
        { $"FROM base{Digest}\nonbuild RUN --mount=type=bind,from=busybox,target=/x ls\n", "ONBUILD RUN --mount from=busybox" },
        { $"FROM base{Digest} AS build\nFROM base{Digest}\nONBUILD COPY --from=build /a /b\n", "ONBUILD COPY --from=build" },
        { $"FROM base{Digest}\nONBUILD ADD \\\n  --from=0 /a /b\n", "ONBUILD COPY --from=0" },
        { $"FROM base{Digest}\nONBUILD FROM alpine\n", "ONBUILD FROM" },
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
            ONBUILD COPY --from=busybox{Digest} /bin/busybox /busybox
            ONBUILD RUN --mount=type=cache,target=/root/.cache ls
            """;

        Assert.Empty(UnpinnedReferences(dockerfile));
    }

    [Fact]
    public void Escape_directive_switches_the_continuation_character()
    {
        // With escape=` a trailing backslash is literal (Windows paths), so the next line is a new instruction.
        var dockerfile = $"# escape=`\nFROM base{Digest}\nRUN dir C:\\\nCOPY --from=0 /a /b\n";

        var problems = UnpinnedReferences(dockerfile);

        Assert.Contains(problems, p => p.StartsWith("COPY --from=0 ", StringComparison.Ordinal));
        Assert.Empty(UnpinnedReferences($"# escape=`\nFROM base{Digest} AS build\nFROM base{Digest}\nCOPY `\n  --from=build /a /b\n"));
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
            var onBuild = false;
            if (keyword == "ONBUILD")
            {
                // P-3: ONBUILD <instruction> runs in a later build; check the instruction it carries.
                if (tokens.Length < 2)
                {
                    problems.Add($"ONBUILD without an instruction ({instruction})");
                    continue;
                }

                tokens = tokens[1..];
                keyword = tokens[0].ToUpperInvariant();
                onBuild = true;
                if (keyword is "FROM" or "ONBUILD" or "MAINTAINER")
                {
                    problems.Add($"ONBUILD {keyword} is not allowed ({instruction})");
                    continue;
                }
            }

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
                        Check(Unquote(flag["--from=".Length..]), onBuild ? "ONBUILD COPY --from=" : "COPY --from=", onBuild);
                    }

                    break;

                case "RUN":
                    foreach (var flag in flags.Where(f => f.StartsWith("--mount=", StringComparison.OrdinalIgnoreCase)))
                    {
                        foreach (var option in Unquote(flag["--mount=".Length..]).Split(','))
                        {
                            if (option.StartsWith("from=", StringComparison.OrdinalIgnoreCase))
                            {
                                Check(Unquote(option["from=".Length..]), onBuild ? "ONBUILD RUN --mount from=" : "RUN --mount from=", onBuild);
                            }
                        }
                    }

                    break;
            }
        }

        return problems;

        void Check(string reference, string what, bool onBuild)
        {
            // An ONBUILD trigger cannot name a stage of this file (it runs in a later build), so only a digest passes.
            if ((onBuild || !IsEarlierStage(reference)) && !PinnedImage.IsMatch(reference))
            {
                problems.Add($"{what}{reference} is not an earlier build stage or an image pinned by digest");
            }
        }

        bool IsEarlierStage(string reference) =>
            earlierStages.Contains(reference, StringComparer.OrdinalIgnoreCase)
            || (int.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < stageIndex);
    }

    private static readonly Regex ParserDirective = new(@"^#\s*([A-Za-z]+)\s*=\s*(\S+)\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// P-3: the continuation (escape) character. Parser directives are read only from the top of the file and stop
    /// at the first blank line, plain comment or instruction, as the BuildKit parser does. Only <c>\</c> and the
    /// backtick are valid.
    /// </summary>
    internal static char EscapeCharacter(string[] lines)
    {
        var escape = '\\';
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var match = ParserDirective.Match(raw.Trim());
            if (!match.Success)
            {
                break;
            }

            var name = match.Groups[1].Value.ToLowerInvariant();
            if (name is not ("escape" or "syntax" or "check") || !seen.Add(name))
            {
                break;
            }

            if (name == "escape")
            {
                var value = match.Groups[2].Value;
                if (value is not ("\\" or "`"))
                {
                    throw new InvalidOperationException($"Invalid escape directive value '{value}'.");
                }

                escape = value[0];
            }
        }

        return escape;
    }

    private static IEnumerable<string> Instructions(string dockerfile)
    {
        var current = new List<string>();
        var lines = dockerfile.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var escape = EscapeCharacter(lines);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('#'))
            {
                continue;
            }

            var continues = line.EndsWith(escape);
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
