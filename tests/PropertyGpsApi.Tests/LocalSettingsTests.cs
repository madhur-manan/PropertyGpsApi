using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using PropertyGpsApi.Common;

namespace PropertyGpsApi.Tests;

/// <summary>
/// L1 / N6: appsettings.Local.json overrides appsettings.json and
/// appsettings.{Environment}.json, but an environment variable or a command-line argument
/// overrides it. It used to be appended after both, so a Database__B2A set for one run was
/// silently ignored in favour of whatever server the local file named.
///
/// Built on WebApplication.CreateBuilder with a throw-away content root, so the source list
/// is the host's real default one, the same as Program.cs.
/// </summary>
public sealed class LocalSettingsTests : IDisposable
{
    private const string Section = "LocalSettingsTest";
    private const string EnvName = Section + "__FromEnv";

    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "local-settings-" + Guid.NewGuid().ToString("N"));

    public LocalSettingsTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), $$"""
            { "{{Section}}": { "BaseAndLocal": "base", "OnlyBase": "base" } }
            """);
        File.WriteAllText(Path.Combine(_root, "appsettings.Development.json"), $$"""
            { "{{Section}}": { "EnvironmentFileAndLocal": "development" } }
            """);
        File.WriteAllText(Path.Combine(_root, LocalSettings.FileName), $$"""
            {
              "{{Section}}": {
                "OnlyLocal": "local",
                "BaseAndLocal": "local",
                "EnvironmentFileAndLocal": "local",
                "FromEnv": "local",
                "FromCmd": "local"
              }
            }
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private IConfiguration Build()
    {
        Environment.SetEnvironmentVariable(EnvName, "environment");
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development",
                ContentRootPath = _root,
                Args = [$"--{Section}:FromCmd=command-line"],
            });
            LocalSettings.Add(builder.Configuration);
            return builder.Configuration;
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvName, null);
        }
    }

    [Fact]
    public void The_local_file_is_read_from_the_content_root() =>
        Assert.Equal("local", Build()[$"{Section}:OnlyLocal"]);

    [Fact]
    public void The_local_file_overrides_appsettings_json()
    {
        var config = Build();
        Assert.Equal("local", config[$"{Section}:BaseAndLocal"]);
        Assert.Equal("base", config[$"{Section}:OnlyBase"]);
    }

    [Fact]
    public void The_local_file_overrides_the_environment_file() =>
        Assert.Equal("local", Build()[$"{Section}:EnvironmentFileAndLocal"]);

    [Fact]
    public void An_environment_variable_overrides_the_local_file() =>
        Assert.Equal("environment", Build()[$"{Section}:FromEnv"]);

    [Fact]
    public void A_command_line_argument_overrides_the_local_file() =>
        Assert.Equal("command-line", Build()[$"{Section}:FromCmd"]);
}
