namespace DotClaw.Agent;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Application configuration loaded from ~/.dotclaw/config.json.
/// Environment variables override file values when set.
/// </summary>
public class DotClawConfig
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotclaw");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    [JsonPropertyName("azure_openai_endpoint")]
    public string? AzureOpenAiEndpoint { get; set; }

    [JsonPropertyName("azure_openai_api_key")]
    public string? AzureOpenAiApiKey { get; set; }

    [JsonPropertyName("azure_openai_model")]
    public string? AzureOpenAiModel { get; set; }

    [JsonPropertyName("sandbox")]
    public bool? Sandbox { get; set; }

    /// <summary>
    /// Loads config from ~/.dotclaw/config.json, then applies environment variable overrides.
    /// Creates a template config file if none exists.
    /// </summary>
    public static DotClawConfig Load()
    {
        var config = new DotClawConfig();

        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ConfigPath);
                config = JsonSerializer.Deserialize<DotClawConfig>(json) ?? config;
            }
            catch (JsonException)
            {
                // Malformed config — fall through to defaults + env overrides
            }
        }
        else
        {
            // Seed a template so the user knows where to configure
            SeedTemplate();
        }

        // Environment variables take precedence
        var envEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(envEndpoint))
            config.AzureOpenAiEndpoint = envEndpoint;

        var envKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
            config.AzureOpenAiApiKey = envKey;

        var envModel = Environment.GetEnvironmentVariable("AZURE_OPENAI_MODEL");
        if (!string.IsNullOrWhiteSpace(envModel))
            config.AzureOpenAiModel = envModel;

        var envSandbox = Environment.GetEnvironmentVariable("DOTCLAW_SANDBOX");
        if (!string.IsNullOrWhiteSpace(envSandbox))
            config.Sandbox = envSandbox.Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

        // Apply defaults
        config.AzureOpenAiModel ??= "gpt-4.1-mini";
        config.Sandbox ??= true;

        return config;
    }

    private static void SeedTemplate()
    {
        Directory.CreateDirectory(ConfigDir);
        var template = new DotClawConfig
        {
            AzureOpenAiEndpoint = "https://YOUR-RESOURCE.openai.azure.com/",
            AzureOpenAiApiKey = "",
            AzureOpenAiModel = "gpt-4.1-mini",
            Sandbox = true,
        };
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(template, options));
    }
}
