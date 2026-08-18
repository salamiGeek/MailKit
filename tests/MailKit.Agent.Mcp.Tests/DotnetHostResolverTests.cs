using System.Diagnostics;

namespace MailKit.Agent.Mcp.Tests;

public class DotnetHostResolverTests
{
    [Test]
    public void PrefersConfiguredDotnetHostPath()
    {
        var command = DotnetHostResolver.Resolve(
            name => name == "DOTNET_HOST_PATH" ? "/custom/dotnet" : null);

        Assert.That(command, Is.EqualTo("/custom/dotnet"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    public void FallsBackToPathCommandWhenDotnetHostPathIsUnavailable(string? configuredPath)
    {
        var command = DotnetHostResolver.Resolve(_ => configuredPath);

        Assert.That(command, Is.EqualTo("dotnet"));
    }

    [Test]
    public async Task ResolvedCommandCanLaunchTheCurrentDotnetHost()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = DotnetHostResolver.Resolve(),
                Arguments = "--version",
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        try
        {
            Assert.That(process.Start(), Is.True);
            var standardOutput = await process.StandardOutput.ReadToEndAsync(cancellation.Token);
            var standardError = await process.StandardError.ReadToEndAsync(cancellation.Token);
            await process.WaitForExitAsync(cancellation.Token);

            Assert.Multiple(() =>
            {
                Assert.That(process.ExitCode, Is.Zero, standardError);
                Assert.That(standardOutput, Is.Not.Empty);
            });
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }
}
