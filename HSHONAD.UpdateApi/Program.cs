using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});
builder.Services.AddHttpClient("github", client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HSHONAD-UpdateApi", "1.0"));
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    client.Timeout = TimeSpan.FromSeconds(15);
});

var app = builder.Build();
app.UseCors();
app.MapGet("/", () => Results.Ok(new { service = "HSHONAD Update API", status = "ok", endpoints = new[] { "/health", "/api/updates/launcher" } }));
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/updates/launcher", async (IHttpClientFactory clients, HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
{
    var owner = configuration["GitHub:Owner"] ?? "HSHNAD234";
    var repository = configuration["GitHub:Repository"] ?? "Launcher-code";
    var client = clients.CreateClient("github");
    using var response = await client.GetAsync($"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/releases/latest", cancellationToken);

    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        return Results.Problem(title: "No published launcher release found", detail: "Publish a GitHub Release with the HSHONAD-Launcher-win-x64.zip asset first.", statusCode: 404);
    if (!response.IsSuccessStatusCode)
        return Results.Problem(title: "GitHub Releases could not be reached", detail: $"GitHub returned HTTP {(int)response.StatusCode}.", statusCode: 502);

    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    using var release = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    var root = release.RootElement;
    var tag = GetString(root, "tag_name");
    var version = tag.TrimStart('v', 'V');
    if (string.IsNullOrWhiteSpace(version))
        return Results.Problem(title: "Release tag is missing a version", statusCode: 502);
    if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        return Results.Problem(title: "Release has no assets", statusCode: 502);

    JsonElement asset = default;
    foreach (var candidate in assets.EnumerateArray())
    {
        if (string.Equals(GetString(candidate, "name"), "HSHONAD-Launcher-win-x64.zip", StringComparison.Ordinal))
        {
            asset = candidate;
            break;
        }
    }
    if (asset.ValueKind == JsonValueKind.Undefined)
        return Results.Problem(title: "Launcher package is missing", detail: "The latest release must include HSHONAD-Launcher-win-x64.zip.", statusCode: 502);

    var downloadUrl = GetString(asset, "browser_download_url");
    if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var downloadUri) ||
        downloadUri.Scheme != Uri.UriSchemeHttps ||
        !string.Equals(downloadUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
        !downloadUri.AbsolutePath.StartsWith($"/{owner}/{repository}/releases/download/", StringComparison.Ordinal))
        return Results.Problem(title: "Release download URL is invalid", statusCode: 502);

    var digest = GetString(asset, "digest");
    var sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest["sha256:".Length..] : null;
    var currentVersion = context.Request.Query["currentVersion"].ToString();
    var updateAvailable = IsNewer(version, currentVersion);

    return Results.Ok(new LauncherUpdateManifest(
        Version: version, Tag: tag, UpdateAvailable: updateAvailable, DownloadUrl: downloadUrl,
        Sha256: sha256, ReleaseNotes: GetString(root, "body"), PublishedAt: GetString(root, "published_at"),
        Message: updateAvailable ? $"HSHONAD Launcher {version} is available." : $"HSHONAD Launcher is up to date ({version})."));
});

app.Run();

static string GetString(JsonElement element, string property) =>
    element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? string.Empty : string.Empty;

static bool IsNewer(string latest, string current)
{
    if (!Version.TryParse(latest, out var latestVersion))
        return !string.Equals(latest, current, StringComparison.OrdinalIgnoreCase);
    if (!Version.TryParse(current, out var currentVersion)) return true;
    return latestVersion > currentVersion;
}

internal sealed record LauncherUpdateManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("update_available")] bool UpdateAvailable,
    [property: JsonPropertyName("download_url")] string DownloadUrl,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("release_notes")] string ReleaseNotes,
    [property: JsonPropertyName("published_at")] string PublishedAt,
    [property: JsonPropertyName("message")] string Message);
