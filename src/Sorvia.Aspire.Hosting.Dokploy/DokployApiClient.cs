using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.Dokploy;

/// <summary>
/// HTTP client for the Dokploy REST API.
/// Wraps the Dokploy OpenAPI endpoints used during deployment:
/// project management, compose deployment, application management, and database provisioning.
/// </summary>
/// <remarks>
/// API reference: https://github.com/Dokploy/cli (auto-generated from OpenAPI spec).
/// Authentication is via the <c>x-api-key</c> HTTP header.
/// All endpoints follow the pattern: POST /api/{group}.{action} or GET /api/{group}.{action}
/// </remarks>
internal sealed class DokployApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _serverUrl;

    /// <summary>JSON options that include null values (Dokploy requires all schema fields to be present).</summary>
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>JSON options that skip null values (for payloads where null fields should be omitted).</summary>
    private static readonly JsonSerializerOptions s_jsonOptionsSkipNull = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public DokployApiClient(string serverUrl, string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _serverUrl = NormalizeServerUrl(serverUrl);

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_serverUrl + "/api/"),
            Timeout = TimeSpan.FromMinutes(5)
        };
        _httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
    }

    internal static string NormalizeServerUrl(string serverUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);

        var normalized = serverUrl.Trim();
        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "https://" + normalized;
        }

        normalized = normalized.TrimEnd('/');
        if (normalized.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"Dokploy server URL '{serverUrl}' is invalid. Enter a full URL or host name. Host names without a scheme default to https://.");
        }

        return uri.ToString().TrimEnd('/');
    }

    private string CreateServerAccessHint(string details)
        => $"Could not reach Dokploy server '{_serverUrl}'. If you entered only a host name, https:// is assumed automatically. If your Dokploy instance only responds over http:// or uses a different URL, update the server URL and try again. {details}";

    /// <summary>
    /// Posts JSON and throws a descriptive exception on failure (includes response body).
    /// </summary>
    private async Task<HttpResponseMessage> PostJsonAsync<T>(string endpoint, T payload, JsonSerializerOptions? options = null, CancellationToken ct = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync(endpoint, payload, options ?? s_jsonOptions, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException(CreateServerAccessHint(ex.Message), ex, ex.StatusCode);
        }

        await EnsureSuccessAsync(response, endpoint, ct).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// Gets and throws a descriptive exception on failure (includes response body).
    /// </summary>
    private async Task<HttpResponseMessage> GetJsonAsync(string endpoint, CancellationToken ct = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(endpoint, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException(CreateServerAccessHint(ex.Message), ex, ex.StatusCode);
        }

        await EnsureSuccessAsync(response, endpoint, ct).ConfigureAwait(false);
        return response;
    }

    private async Task<DokploySearchResults<T>> SearchAsync<T>(
        string endpoint,
        IReadOnlyDictionary<string, string?> query,
        CancellationToken ct = default)
    {
        var path = BuildPath(endpoint, query);
        using var response = await GetJsonAsync(path, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<DokploySearchResults<T>>(s_jsonOptions, ct).ConfigureAwait(false)
            ?? new DokploySearchResults<T>();
    }

    private async Task<JsonDocument> GetDocumentAsync(
        string endpoint,
        IReadOnlyDictionary<string, string?> query,
        CancellationToken ct = default)
    {
        using var response = await GetJsonAsync(BuildPath(endpoint, query), ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Dokploy returned null payload for {endpoint}.");
    }

    private static string BuildPath(string endpoint, IReadOnlyDictionary<string, string?> query)
    {
        var queryString = string.Join("&",
            query.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                 .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));

        return string.IsNullOrEmpty(queryString) ? endpoint : $"{endpoint}?{queryString}";
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string endpoint, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Dokploy API {endpoint} returned {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}");
        }
    }

    // ── Project endpoints ─────────────────────────────────────────────

    /// <summary>
    /// Creates a new Dokploy project to group all deployed services.
    /// POST /project.create { name, description?, env? }
    /// Returns nested: { project: { projectId, name, ... }, environment: { environmentId, name, ... } }
    /// </summary>
    public async Task<DokployProject> CreateProjectAsync(
        string name,
        string? description = null,
        string? environmentName = null,
        CancellationToken ct = default)
    {
        var payload = new { name, description, env = environmentName };
        using var response = await PostJsonAsync("project.create", payload, s_jsonOptionsSkipNull, ct).ConfigureAwait(false);
        var wrapper = await response.Content.ReadFromJsonAsync<DokployProjectCreateResponse>(s_jsonOptions, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Dokploy returned null project.");

        // Flatten the nested response into our standard DokployProject model
        var project = wrapper.Project ?? throw new InvalidOperationException("Dokploy returned null project in create response.");
        if (wrapper.Environment is { } env)
        {
            project = project with { Environments = [env] };
        }
        return project;
    }

    /// <summary>
    /// Creates a new Dokploy environment within an existing project.
    /// POST /environment.create { name, description?, projectId }
    /// </summary>
    public async Task<DokployProjectEnvironment> CreateEnvironmentAsync(
        string name,
        string projectId,
        string? description = null,
        CancellationToken ct = default)
    {
        var payload = new { name, description, projectId };
        using var response = await PostJsonAsync("environment.create", payload, s_jsonOptionsSkipNull, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<DokployProjectEnvironment>(s_jsonOptions, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Dokploy returned null environment.");
    }

    /// <summary>
    /// Lists all projects on the Dokploy instance.
    /// GET /project.all
    /// </summary>
    public async Task<DokployProject[]> ListProjectsAsync(CancellationToken ct = default)
    {
        using var response = await GetJsonAsync("project.all", ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<DokployProject[]>(s_jsonOptions, ct) ?? [];
    }

    // ── Compose endpoints ──────────────────────────────────────────────

    /// <summary>
    /// Creates a Docker Compose service on Dokploy.
    /// POST /compose.create { name, environmentId, description?, serverId? }
    /// </summary>
    public async Task<DokployCompose> CreateComposeAsync(
        string name, string environmentId, string? description = null, string? serverId = null, CancellationToken ct = default)
    {
        var payload = new { name, environmentId, description, serverId };
        using var response = await PostJsonAsync("compose.create", payload, s_jsonOptionsSkipNull, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<DokployCompose>(s_jsonOptions, ct)
            ?? throw new InvalidOperationException("Dokploy returned null compose service.");
    }

    /// <summary>
    /// Updates the Docker Compose file content for a compose service.
    /// Sets the source type to "raw" since we're providing the compose content directly.
    /// POST /compose.update { composeId, composeFile, sourceType }
    /// </summary>
    public async Task UpdateComposeAsync(string composeId, string composeFile, string? env = null, CancellationToken ct = default)
    {
        var payload = new { composeId, composeFile, sourceType = "raw", env };
        using var _ = await PostJsonAsync("compose.update", payload, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Triggers deployment of a Docker Compose service.
    /// POST /compose.deploy { composeId }
    /// </summary>
    public async Task DeployComposeAsync(string composeId, CancellationToken ct = default)
    {
        var payload = new { composeId };
        using var _ = await PostJsonAsync("compose.deploy", payload, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Searches Dokploy compose services by name and environment.
    /// GET /compose.search
    /// </summary>
    public async Task<DokployCompose[]> SearchComposesAsync(
        string name,
        string environmentId,
        CancellationToken ct = default)
    {
        var result = await SearchAsync<DokployCompose>(
            "compose.search",
            new Dictionary<string, string?>
            {
                ["name"] = name,
                ["environmentId"] = environmentId
            },
            ct).ConfigureAwait(false);

        return result.Items;
    }

    /// <summary>
    /// Reads a Dokploy compose service including generated domains and deployment metadata.
    /// GET /compose.one
    /// </summary>
    public Task<JsonDocument> GetComposeAsync(string composeId, CancellationToken ct = default)
        => GetDocumentAsync(
            "compose.one",
            new Dictionary<string, string?>
            {
                ["composeId"] = composeId
            },
            ct);

    // ── Application endpoints ──────────────────────────────────────────

    /// <summary>
    /// Creates a Dokploy application resource (for individual service deployments).
    /// POST /application.create { name, environmentId, description?, serverId? }
    /// </summary>
    public async Task<DokployApplication> CreateApplicationAsync(
        string name, string environmentId, string? appName = null, string? description = null, string? serverId = null, CancellationToken ct = default)
    {
        var payload = new { name, appName, environmentId, description, serverId };
        using var response = await PostJsonAsync("application.create", payload, s_jsonOptionsSkipNull, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<DokployApplication>(s_jsonOptions, ct)
            ?? throw new InvalidOperationException("Dokploy returned null application.");
    }

    /// <summary>
    /// Searches Dokploy applications by name and environment.
    /// GET /application.search
    /// </summary>
    public async Task<DokployApplication[]> SearchApplicationsAsync(
        string name,
        string environmentId,
        CancellationToken ct = default)
    {
        var result = await SearchAsync<DokployApplication>(
            "application.search",
            new Dictionary<string, string?>
            {
                ["name"] = name,
                ["environmentId"] = environmentId
            },
            ct).ConfigureAwait(false);

        return result.Items;
    }

    /// <summary>
    /// Sets the Docker image source for an application.
    /// POST /application.saveDockerProvider { applicationId, dockerImage, username, password, registryUrl }
    /// All fields are required by the Dokploy schema; null values are sent explicitly.
    /// </summary>
    public async Task SaveDockerProviderAsync(
        string applicationId, string dockerImage, string? username = null, string? password = null,
        string? registryUrl = null, CancellationToken ct = default)
    {
        // Dokploy requires all 5 fields — null is valid for optional ones but the key must be present
        var payload = new { applicationId, dockerImage, username, password, registryUrl };
        using var _ = await PostJsonAsync("application.saveDockerProvider", payload, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates an application's properties (e.g., link a registry, set startup command).
    /// POST /application.update { applicationId, ... }
    /// </summary>
    public async Task UpdateApplicationAsync(
        string applicationId,
        string? registryId = null,
        string? command = null,
        string[]? args = null,
        CancellationToken ct = default)
    {
        var payload = new { applicationId, registryId, command, args };
        using var _ = await PostJsonAsync("application.update", payload, s_jsonOptionsSkipNull, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deploys an application.
    /// POST /application.deploy { applicationId }
    /// </summary>
    public async Task DeployApplicationAsync(string applicationId, string? title = null, string? description = null, CancellationToken ct = default)
    {
        var payload = new { applicationId, title, description };
        using var _ = await PostJsonAsync("application.deploy", payload, ct: ct).ConfigureAwait(false);
    }

    // ── Postgres endpoints ─────────────────────────────────────────────

    /// <summary>
    /// Creates a PostgreSQL database on Dokploy.
    /// POST /postgres.create { name, databaseName, databaseUser, databasePassword, dockerImage, environmentId, description?, serverId? }
    /// </summary>
    public async Task<DokployPostgres> CreatePostgresAsync(
        string name, string environmentId, string? appName = null, string? databaseName = null, string? databaseUser = null,
        string? databasePassword = null, string? dockerImage = null, string? description = null,
        string? serverId = null, CancellationToken ct = default)
    {
        var payload = new { name, appName, databaseName, databaseUser, databasePassword, dockerImage, environmentId, description, serverId };
        using var response = await PostJsonAsync("postgres.create", payload, s_jsonOptionsSkipNull, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<DokployPostgres>(s_jsonOptions, ct)
            ?? throw new InvalidOperationException("Dokploy returned null postgres instance.");
    }

    /// <summary>
    /// Updates a PostgreSQL database on Dokploy.
    /// POST /postgres.update { postgresId, appName?, databaseName?, databaseUser?, databasePassword?, dockerImage? }
    /// </summary>
    public async Task UpdatePostgresAsync(
        string postgresId,
        string? appName = null,
        string? databaseName = null,
        string? databaseUser = null,
        string? databasePassword = null,
        string? dockerImage = null,
        CancellationToken ct = default)
    {
        var payload = new { postgresId, appName, databaseName, databaseUser, databasePassword, dockerImage };
        using var _ = await PostJsonAsync("postgres.update", payload, s_jsonOptionsSkipNull, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Searches Dokploy PostgreSQL databases by name and environment.
    /// GET /postgres.search
    /// </summary>
    public async Task<DokployPostgres[]> SearchPostgresAsync(
        string name,
        string environmentId,
        CancellationToken ct = default)
    {
        var result = await SearchAsync<DokployPostgres>(
            "postgres.search",
            new Dictionary<string, string?>
            {
                ["name"] = name,
                ["environmentId"] = environmentId
            },
            ct).ConfigureAwait(false);

        return result.Items;
    }

    // ── Redis endpoints ────────────────────────────────────────────────

    /// <summary>
    /// Creates a Redis database on Dokploy.
    /// POST /redis.create { name, databasePassword, dockerImage, environmentId, description?, serverId? }
    /// </summary>
    public async Task<DokployRedis> CreateRedisAsync(
        string name, string environmentId, string? appName = null, string? databasePassword = null, string? dockerImage = null,
        string? description = null, string? serverId = null, CancellationToken ct = default)
    {
        var payload = new { name, appName, databasePassword, dockerImage, environmentId, description, serverId };
        using var response = await PostJsonAsync("redis.create", payload, s_jsonOptionsSkipNull, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<DokployRedis>(s_jsonOptions, ct)
            ?? throw new InvalidOperationException("Dokploy returned null redis instance.");
    }

    /// <summary>
    /// Searches Dokploy Redis databases by name and environment.
    /// GET /redis.search
    /// </summary>
    public async Task<DokployRedis[]> SearchRedisAsync(
        string name,
        string environmentId,
        CancellationToken ct = default)
    {
        var result = await SearchAsync<DokployRedis>(
            "redis.search",
            new Dictionary<string, string?>
            {
                ["name"] = name,
                ["environmentId"] = environmentId
            },
            ct).ConfigureAwait(false);

        return result.Items;
    }

    // ── MySQL endpoints ────────────────────────────────────────────────

    /// <summary>
    /// Creates a MySQL database on Dokploy.
    /// POST /mysql.create { name, databaseName, databaseUser, databasePassword, databaseRootPassword, dockerImage, environmentId, description?, serverId? }
    /// </summary>
    public async Task<DokployMySql> CreateMySqlAsync(
        string name, string environmentId, string? appName = null, string? databaseName = null, string? databaseUser = null,
        string? databasePassword = null, string? databaseRootPassword = null, string? dockerImage = null,
        string? description = null, string? serverId = null, CancellationToken ct = default)
    {
        var payload = new { name, appName, databaseName, databaseUser, databasePassword, databaseRootPassword, dockerImage, environmentId, description, serverId };
        using var response = await PostJsonAsync("mysql.create", payload, s_jsonOptionsSkipNull, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<DokployMySql>(s_jsonOptions, ct)
            ?? throw new InvalidOperationException("Dokploy returned null mysql instance.");
    }

    /// <summary>
    /// Searches Dokploy MySQL databases by name and environment.
    /// GET /mysql.search
    /// </summary>
    public async Task<DokployMySql[]> SearchMySqlAsync(
        string name,
        string environmentId,
        CancellationToken ct = default)
    {
        var result = await SearchAsync<DokployMySql>(
            "mysql.search",
            new Dictionary<string, string?>
            {
                ["name"] = name,
                ["environmentId"] = environmentId
            },
            ct).ConfigureAwait(false);

        return result.Items;
    }

    // ── MariaDB endpoints ──────────────────────────────────────────────

    /// <summary>
    /// Creates a MariaDB database on Dokploy.
    /// POST /mariadb.create { name, databaseName, databaseUser, databasePassword, databaseRootPassword, dockerImage, environmentId, description?, serverId? }
    /// </summary>
    public async Task<DokployMariaDB> CreateMariaDBAsync(
        string name, string environmentId, string? appName = null, string? databaseName = null, string? databaseUser = null,
        string? databasePassword = null, string? databaseRootPassword = null, string? dockerImage = null,
        string? description = null, string? serverId = null, CancellationToken ct = default)
    {
        var payload = new { name, appName, databaseName, databaseUser, databasePassword, databaseRootPassword, dockerImage, environmentId, description, serverId };
        using var response = await PostJsonAsync("mariadb.create", payload, s_jsonOptionsSkipNull, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<DokployMariaDB>(s_jsonOptions, ct)
            ?? throw new InvalidOperationException("Dokploy returned null mariadb instance.");
    }

    /// <summary>
    /// Searches Dokploy MariaDB databases by name and environment.
    /// GET /mariadb.search
    /// </summary>
    public async Task<DokployMariaDB[]> SearchMariaDBAsync(
        string name,
        string environmentId,
        CancellationToken ct = default)
    {
        var result = await SearchAsync<DokployMariaDB>(
            "mariadb.search",
            new Dictionary<string, string?>
            {
                ["name"] = name,
                ["environmentId"] = environmentId
            },
            ct).ConfigureAwait(false);

        return result.Items;
    }

    // ── MongoDB endpoints ──────────────────────────────────────────────

    /// <summary>
    /// Creates a MongoDB database on Dokploy.
    /// POST /mongo.create { name, databaseUser, databasePassword, dockerImage, environmentId, description?, serverId? }
    /// </summary>
    public async Task<DokployMongo> CreateMongoAsync(
        string name, string environmentId, string? appName = null, string? databaseUser = null, string? databasePassword = null,
        string? dockerImage = null, string? description = null, string? serverId = null, CancellationToken ct = default)
    {
        var payload = new { name, appName, databaseUser, databasePassword, dockerImage, environmentId, description, serverId };
        using var response = await PostJsonAsync("mongo.create", payload, s_jsonOptionsSkipNull, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<DokployMongo>(s_jsonOptions, ct)
            ?? throw new InvalidOperationException("Dokploy returned null mongo instance.");
    }

    /// <summary>
    /// Searches Dokploy MongoDB databases by name and environment.
    /// GET /mongo.search
    /// </summary>
    public async Task<DokployMongo[]> SearchMongoAsync(
        string name,
        string environmentId,
        CancellationToken ct = default)
    {
        var result = await SearchAsync<DokployMongo>(
            "mongo.search",
            new Dictionary<string, string?>
            {
                ["name"] = name,
                ["environmentId"] = environmentId
            },
            ct).ConfigureAwait(false);

        return result.Items;
    }

    // ── Environment endpoints ──────────────────────────────────────────

    /// <summary>
    /// Saves environment variables for an application.
    /// POST /application.saveEnvironment { applicationId, env, buildArgs, buildSecrets, createEnvFile }
    /// </summary>
    public async Task SaveApplicationEnvironmentAsync(
        string applicationId, string env, CancellationToken ct = default)
    {
        var payload = new
        {
            applicationId,
            env,
            buildArgs = (string?)null,
            buildSecrets = (string?)null,
            createEnvFile = true
        };
        using var _ = await PostJsonAsync("application.saveEnvironment", payload, ct: ct).ConfigureAwait(false);
    }

    // ── Registry endpoints ─────────────────────────────────────────────

    /// <summary>
    /// Creates a container registry on Dokploy.
    /// POST /registry.create { registryName, username, password, registryUrl, registryType, imagePrefix? }
    /// </summary>
    public async Task<DokployRegistry> CreateRegistryAsync(
        string registryName, string username, string password, string registryUrl,
        string? imagePrefix = null, string? serverId = null, CancellationToken ct = default)
    {
        var payload = new { registryName, username, password, registryUrl, registryType = "cloud", imagePrefix, serverId };
        using var response = await PostJsonAsync("registry.create", payload, s_jsonOptionsSkipNull, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<DokployRegistry>(s_jsonOptions, ct).ConfigureAwait(false)
            ?? new DokployRegistry
            {
                RegistryName = registryName,
                RegistryUrl = registryUrl,
                ImagePrefix = imagePrefix
            };
    }

    /// <summary>
    /// Lists all registries on the Dokploy instance.
    /// GET /registry.all
    /// </summary>
    public async Task<DokployRegistry[]> ListRegistriesAsync(CancellationToken ct = default)
    {
        using var response = await GetJsonAsync("registry.all", ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<DokployRegistry[]>(s_jsonOptions, ct) ?? [];
    }

    /// <summary>
    /// Updates an existing Dokploy registry so node pull credentials stay in sync with the managed project registry.
    /// POST /registry.update
    /// </summary>
    public async Task UpdateRegistryAsync(
        string registryId,
        string registryName,
        string username,
        string password,
        string registryUrl,
        string? imagePrefix = null,
        string? serverId = null,
        CancellationToken ct = default)
    {
        var payload = new
        {
            registryId,
            registryName,
            username,
            password,
            registryUrl,
            registryType = "cloud",
            imagePrefix,
            serverId
        };

        using var _ = await PostJsonAsync("registry.update", payload, s_jsonOptionsSkipNull, ct).ConfigureAwait(false);
    }

    // ── Domain endpoints ───────────────────────────────────────────────

    /// <summary>
    /// Creates a domain/routing rule for an application (via Traefik).
    /// POST /domain.create { host, port?, applicationId, domainType? }
    /// </summary>
    public async Task<DokployDomain> CreateDomainAsync(
        string host, int? port = null, string? applicationId = null,
        bool https = false, string? certificateType = null, CancellationToken ct = default)
    {
        var payload = new
        {
            host,
            port,
            applicationId,
            domainType = applicationId is not null ? "application" : (string?)null,
            https,
            certificateType = certificateType ?? (https ? "letsencrypt" : "none")
        };
        using var response = await PostJsonAsync("domain.create", payload, ct: ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<DokployDomain>(s_jsonOptions, ct)
            ?? throw new InvalidOperationException("Dokploy returned null domain.");
    }

    /// <summary>
    /// Creates a domain/routing rule for a compose service (via Traefik).
    /// POST /domain.create { host, port?, composeId, serviceName, domainType = compose }
    /// </summary>
    public async Task<DokployDomain> CreateComposeDomainAsync(
        string composeId,
        string serviceName,
        string host,
        int port,
        bool https = true,
        string? certificateType = null,
        CancellationToken ct = default)
    {
        var payload = new
        {
            host,
            port,
            composeId,
            serviceName,
            domainType = "compose",
            https,
            certificateType = certificateType ?? (https ? "letsencrypt" : "none")
        };

        using var response = await PostJsonAsync("domain.create", payload, ct: ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<DokployDomain>(s_jsonOptions, ct)
            ?? throw new InvalidOperationException("Dokploy returned null compose domain.");
    }

    /// <summary>
    /// Reads domains attached to a compose service.
    /// GET /domain.byComposeId
    /// </summary>
    public Task<JsonDocument> GetComposeDomainsAsync(string composeId, CancellationToken ct = default)
        => GetDocumentAsync(
            "domain.byComposeId",
            new Dictionary<string, string?>
            {
                ["composeId"] = composeId
            },
            ct);

    /// <summary>
    /// Reads domains attached to an application.
    /// GET /domain.byApplicationId
    /// </summary>
    public Task<JsonDocument> GetApplicationDomainsAsync(string applicationId, CancellationToken ct = default)
        => GetDocumentAsync(
            "domain.byApplicationId",
            new Dictionary<string, string?>
            {
                ["applicationId"] = applicationId
            },
            ct);

    /// <summary>
    /// Deletes a domain/routing rule from Dokploy.
    /// POST /domain.delete { domainId }
    /// </summary>
    public async Task DeleteDomainAsync(string domainId, CancellationToken ct = default)
    {
        var payload = new { domainId };
        using var _ = await PostJsonAsync("domain.delete", payload, ct: ct).ConfigureAwait(false);
    }

    // ── Port endpoints ─────────────────────────────────────────────────

    /// <summary>
    /// Creates a direct port mapping for an application (host ↔ container).
    /// POST /port.create { publishedPort, targetPort, protocol, publishMode, applicationId }
    /// </summary>
    public async Task CreatePortAsync(
        int publishedPort, int targetPort, string applicationId,
        string protocol = "tcp", string publishMode = "host", CancellationToken ct = default)
    {
        var payload = new { publishedPort, targetPort, protocol, publishMode, applicationId };
        using var _ = await PostJsonAsync("port.create", payload, ct: ct).ConfigureAwait(false);
    }

    // ── Database deploy endpoints ──────────────────────────────────────

    /// <summary>
    /// Triggers deployment of a PostgreSQL database.
    /// POST /postgres.deploy { postgresId }
    /// </summary>
    public async Task DeployPostgresAsync(string postgresId, CancellationToken ct = default)
    {
        var payload = new { postgresId };
        using var _ = await PostJsonAsync("postgres.deploy", payload, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a PostgreSQL database.
    /// POST /postgres.remove { postgresId }
    /// </summary>
    public async Task RemovePostgresAsync(string postgresId, CancellationToken ct = default)
    {
        var payload = new { postgresId };
        using var _ = await PostJsonAsync("postgres.remove", payload, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Triggers deployment of a Redis instance.
    /// POST /redis.deploy { redisId }
    /// </summary>
    public async Task DeployRedisAsync(string redisId, CancellationToken ct = default)
    {
        var payload = new { redisId };
        using var _ = await PostJsonAsync("redis.deploy", payload, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Triggers deployment of a MySQL database.
    /// POST /mysql.deploy { mysqlId }
    /// </summary>
    public async Task DeployMySqlAsync(string mysqlId, CancellationToken ct = default)
    {
        var payload = new { mysqlId };
        using var _ = await PostJsonAsync("mysql.deploy", payload, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Triggers deployment of a MariaDB database.
    /// POST /mariadb.deploy { mariadbId }
    /// </summary>
    public async Task DeployMariaDBAsync(string mariadbId, CancellationToken ct = default)
    {
        var payload = new { mariadbId };
        using var _ = await PostJsonAsync("mariadb.deploy", payload, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Triggers deployment of a MongoDB instance.
    /// POST /mongo.deploy { mongoId }
    /// </summary>
    public async Task DeployMongoAsync(string mongoId, CancellationToken ct = default)
    {
        var payload = new { mongoId };
        using var _ = await PostJsonAsync("mongo.deploy", payload, ct: ct).ConfigureAwait(false);
    }

    /// <summary>Reads a Dokploy PostgreSQL instance.</summary>
    public Task<JsonDocument> GetPostgresAsync(string postgresId, CancellationToken ct = default)
        => GetDocumentAsync("postgres.one", new Dictionary<string, string?> { ["postgresId"] = postgresId }, ct);

    /// <summary>Reads a Dokploy Redis instance.</summary>
    public Task<JsonDocument> GetRedisAsync(string redisId, CancellationToken ct = default)
        => GetDocumentAsync("redis.one", new Dictionary<string, string?> { ["redisId"] = redisId }, ct);

    /// <summary>Reads a Dokploy MySQL instance.</summary>
    public Task<JsonDocument> GetMySqlAsync(string mysqlId, CancellationToken ct = default)
        => GetDocumentAsync("mysql.one", new Dictionary<string, string?> { ["mysqlId"] = mysqlId }, ct);

    /// <summary>Reads a Dokploy MariaDB instance.</summary>
    public Task<JsonDocument> GetMariaDbAsync(string mariadbId, CancellationToken ct = default)
        => GetDocumentAsync("mariadb.one", new Dictionary<string, string?> { ["mariadbId"] = mariadbId }, ct);

    /// <summary>Reads a Dokploy MongoDB instance.</summary>
    public Task<JsonDocument> GetMongoAsync(string mongoId, CancellationToken ct = default)
        => GetDocumentAsync("mongo.one", new Dictionary<string, string?> { ["mongoId"] = mongoId }, ct);

    public void Dispose() => _httpClient.Dispose();
}

// ── API response models ────────────────────────────────────────────────────

/// <summary>Dokploy project, returned from project.all (flat) and used as normalized model.</summary>
internal sealed record DokployProject
{
    [JsonPropertyName("projectId")]
    public string ProjectId { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("environments")]
    public DokployProjectEnvironment[]? Environments { get; init; }
}

/// <summary>
/// Response from project.create — nested: { project: {...}, environment: {...} }.
/// Different from the flat structure returned by project.all.
/// </summary>
internal sealed record DokployProjectCreateResponse
{
    [JsonPropertyName("project")]
    public DokployProject? Project { get; init; }

    [JsonPropertyName("environment")]
    public DokployProjectEnvironment? Environment { get; init; }
}

internal sealed record DokploySearchResults<T>
{
    [JsonPropertyName("items")]
    public T[] Items { get; init; } = [];
}

/// <summary>A Dokploy project environment (each project has at least one).</summary>
internal sealed record DokployProjectEnvironment
{
    [JsonPropertyName("environmentId")]
    public string EnvironmentId { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";
}

/// <summary>Dokploy compose service, returned from compose.create.</summary>
internal sealed record DokployCompose
{
    [JsonPropertyName("composeId")]
    public string ComposeId { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("environmentId")]
    public string? EnvironmentId { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>Dokploy application, returned from application.create.</summary>
internal sealed record DokployApplication
{
    [JsonPropertyName("applicationId")]
    public string ApplicationId { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("appName")]
    public string AppName { get; init; } = "";

    [JsonPropertyName("environmentId")]
    public string? EnvironmentId { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>Dokploy PostgreSQL instance, returned from postgres.create.</summary>
internal sealed record DokployPostgres
{
    [JsonPropertyName("postgresId")]
    public string PostgresId { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("appName")]
    public string AppName { get; init; } = "";

    [JsonPropertyName("environmentId")]
    public string? EnvironmentId { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>Dokploy Redis instance, returned from redis.create.</summary>
internal sealed record DokployRedis
{
    [JsonPropertyName("redisId")]
    public string RedisId { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("environmentId")]
    public string? EnvironmentId { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>Dokploy MySQL instance, returned from mysql.create.</summary>
internal sealed record DokployMySql
{
    [JsonPropertyName("mysqlId")]
    public string MySqlId { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("environmentId")]
    public string? EnvironmentId { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>Dokploy MariaDB instance, returned from mariadb.create.</summary>
internal sealed record DokployMariaDB
{
    [JsonPropertyName("mariadbId")]
    public string MariaDBId { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("environmentId")]
    public string? EnvironmentId { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>Dokploy MongoDB instance, returned from mongo.create.</summary>
internal sealed record DokployMongo
{
    [JsonPropertyName("mongoId")]
    public string MongoId { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("environmentId")]
    public string? EnvironmentId { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>Dokploy container registry, returned from registry.create / registry.all.</summary>
internal sealed record DokployRegistry
{
    [JsonPropertyName("registryId")]
    public string RegistryId { get; init; } = "";

    [JsonPropertyName("registryName")]
    public string RegistryName { get; init; } = "";

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("password")]
    public string? Password { get; init; }

    [JsonPropertyName("registryUrl")]
    public string RegistryUrl { get; init; } = "";

    [JsonPropertyName("imagePrefix")]
    public string? ImagePrefix { get; init; }
}

/// <summary>Dokploy domain, returned from domain.create.</summary>
internal sealed record DokployDomain
{
    [JsonPropertyName("domainId")]
    public string DomainId { get; init; } = "";

    [JsonPropertyName("host")]
    public string Host { get; init; } = "";
}
