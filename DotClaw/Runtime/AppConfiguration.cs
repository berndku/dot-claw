using Microsoft.Extensions.Configuration;

namespace DotClaw.Runtime;

/// <summary>
/// Builds a shared IConfiguration from appsettings.json + appsettings.local.json + environment variables.
/// appsettings.local.json is gitignored and holds actual secrets for local development.
/// Environment variables override everything (for CI/production).
/// </summary>
public static class AppConfiguration
{
    private static IConfiguration? _instance;

    public static IConfiguration Instance => _instance ??= Build();

    private static IConfiguration Build()
    {
        // Search upward from cwd to find appsettings.json at the solution root
        var basePath = FindSettingsDirectory() ?? Directory.GetCurrentDirectory();

        return new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
    }

    private static string? FindSettingsDirectory()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "appsettings.json")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }
}
