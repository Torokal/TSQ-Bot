namespace ToroSquad.Tests.Unit;

/// <summary>
/// The production image restores before copying the sources (layer caching, locked mode). Every project under src/ must
/// therefore be copied into the restore layer explicitly — found in production: the Formula 1 project was missing, so the
/// Railway build failed with NETSDK1004 (the previous deployment kept running).
/// </summary>
public sealed class DockerfileTests
{
    [Fact]
    public void Every_source_project_and_its_lock_file_is_copied_before_the_locked_restore()
    {
        var root = CommandManifestTests.RepoRoot();
        var dockerfile = File.ReadAllLines(Path.Combine(root, "Dockerfile"));
        var restoreLine = Array.FindIndex(dockerfile, l => l.StartsWith("RUN dotnet restore", StringComparison.Ordinal));
        restoreLine.Should().BePositive();
        var restoreLayer = string.Join("\n", dockerfile.Take(restoreLine));

        foreach (var project in Directory.GetDirectories(Path.Combine(root, "src")).Select(Path.GetFileName))
        {
            var csproj = $"src/{project}/{project}.csproj";
            if (!File.Exists(Path.Combine(root, csproj)))
                continue;
            restoreLayer.Should().Contain(csproj, $"{project} must be restored in the image");
            restoreLayer.Should().Contain($"src/{project}/packages.lock.json", $"{project} restores in locked mode");
        }
    }
}
