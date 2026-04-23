#pragma warning disable ASPIREPIPELINES001 // This resource defines its own custom pipeline steps and is not compatible with the stock Docker Compose pipeline. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.Resources;
using Aspire.Hosting.Dokploy.Annotations;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aspire.Hosting.Dokploy;



/// <summary>
/// Represents a Dokploy deployment target environment in the Aspire application model.
/// When publishing, the Aspire pipeline will use this resource to determine how to
/// deploy the application to a Dokploy instance.
/// </summary>
/// <remarks>
/// <para>
/// Dokploy is a self-hosted PaaS (Platform as a Service) that simplifies the deployment
/// and management of applications and databases. This resource represents a single
/// Dokploy server instance that can host the application.
/// </para>
/// <para>Inherits Docker Compose publishing behavior and replaces the deploy phase with Dokploy delivery.</para>
/// <para>See: https://dokploy.com and https://github.com/Dokploy/cli</para>
/// </remarks>
public sealed partial class DokployDeploymentExecutor
{
    const string DefaultServerUrl = "https://panel.dokploy.com";
    private const string DefaultDeploymentEnvironmentName = "production";

    private static readonly TimeSpan s_registryBootstrapTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_registryProbeInterval = TimeSpan.FromSeconds(5);

    private HashSet<string> _excludedComposeServices = [];
    private Dictionary<string, PublishedComposeServiceSnapshot> _publishedComposeServices = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The parameter resource providing the Dokploy server URL.
    /// When set, takes precedence over the <see cref="ServerUrl"/> string value.
    /// </summary>
    public ParameterResource? ServerUrlParameter { get; set; }

    /// <summary>
    /// The parameter resource providing the Dokploy API key.
    /// When set, takes precedence over the <see cref="ApiKey"/> string value.
    /// </summary>
    public ParameterResource? ApiKeyParameter { get; set; }

    /// <summary>
    /// The parameter resource providing the Dokploy project name.
    /// When set, it determines which Dokploy project receives the deployment.
    /// </summary>
    public ParameterResource? ProjectNameParameter { get; set; }

    /// <summary>
    /// The parameter resource providing the Dokploy server ID.
    /// When set, takes precedence over the <see cref="ServerId"/> string value.
    /// </summary>
    public ParameterResource? ServerIdParameter { get; set; }

    /// <summary>
    /// The parameter resource providing the Dokploy environment name.
    /// When set, takes precedence over the <see cref="DeploymentEnvironmentName"/> string value.
    /// </summary>
    public ParameterResource? DeploymentEnvironmentNameParameter { get; set; }

    /// <summary>
    /// The default container registry for resources that don't have an explicit
    /// <c>WithContainerRegistry</c> call. When set, project resources are configured to
    /// push their images to this registry during publish, so Dokploy can pull them.
    /// </summary>
    public IContainerRegistry? DefaultContainerRegistry { get; set; }

    internal IResourceBuilder<DockerComposeAspireDashboardResource>? Dashboard { get; set; }

    private sealed record DokployAutoRegistry(
        string RegistryName,
        string ComposeName,
        string RegistryHost,
        string RegistryUrl,
        string ImagePrefix,
        string Username,
        string Password,
        string HtpasswdLine,
        string? RegistryId,
        string ComposeId);

    private sealed record DokployDatabaseConnection(
        string Host,
        int Port,
        string? DatabaseName,
        string? Username,
        string? Password,
        string? ConnectionString);

    /// <summary>
    /// Resolves the server URL from the generated parameter or an explicit override.
    /// </summary>
    internal async Task<string?> ResolveServerUrlAsync(CancellationToken ct)
    {
        if (ServerUrlParameter is not null)
            return await ServerUrlParameter.GetValueAsync(ct).ConfigureAwait(false) ?? DefaultServerUrl;

        return DefaultServerUrl;
    }

    /// <summary>
    /// Resolves the API key from the generated parameter or an explicit override.
    /// </summary>
    internal async Task<string?> ResolveApiKeyAsync(CancellationToken ct)
    {
        if (ApiKeyParameter is null)
            throw new InvalidOperationException($"Dokploy API key is required but not configured. Please provide an API key via the '{nameof(ApiKeyParameter)}' property or ensure the parameter value is set during deployment.");

        return await ApiKeyParameter.GetValueAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the Dokploy project name from the generated parameter or the resource name.
    /// </summary>
    internal async Task<string> ResolveProjectNameAsync(CancellationToken ct)
    {
        if (ProjectNameParameter is not null)
        {
            var value = await ProjectNameParameter.GetValueAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        throw new InvalidOperationException($"Dokploy project name is required but not configured. Please provide a project name via the '{nameof(ProjectNameParameter)}' property or ensure the parameter value is set during deployment.");
    }

    /// <summary>
    /// Resolves the Dokploy environment name used for deployments within the target project.
    /// </summary>
    internal async Task<string> ResolveDeploymentEnvironmentNameAsync(CancellationToken ct)
    {
        string? value = null;

        if (DeploymentEnvironmentNameParameter is not null)
        {
            value = await DeploymentEnvironmentNameParameter.GetValueAsync(ct).ConfigureAwait(false);
        }

        return NormalizeDokployEnvironmentName(value);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DokployDeploymentExecutor"/> class.
    /// </summary>
    /// <param name="name">The name of the Dokploy environment resource.</param>
    // public DokployDeploymentExecutor(string name) : base(name)
    // {
    //     RemoveDockerComposePipeline();
    //     AppendComposeFileCallback(RemoveDokploySpecificResourcesFromCompose);

    //     Annotations.Add(new PipelineStepAnnotation((factoryContext) =>
    //     {
    //         var publishStep = new PipelineStep
    //         {
    //             Name = $"publish-{Name}",
    //             Description = $"Publishes the Docker Compose environment configuration for {Name}.",
    //             Action = PublishWithDockerAsync
    //         };
    //         publishStep.RequiredBy(WellKnownPipelineSteps.Publish);

    //         var prepareStep = new PipelineStep
    //         {
    //             Name = $"prepare-{Name}",
    //             Description = $"Prepares the Docker Compose environment {Name} for deployment.",
    //             Action = PrepareWithDockerAsync
    //         };
    //         prepareStep.DependsOn(WellKnownPipelineSteps.Publish);
    //         prepareStep.DependsOn(WellKnownPipelineSteps.Build);

    //         var deployStep = new PipelineStep
    //         {
    //             Name = $"deploy-{Name}",
    //             Description = $"Deploys Aspire resources to Dokploy instance for {Name}.",
    //             Action = ctx => DeployToDokployAsync(ctx, (DokployDeploymentExecutor)factoryContext.Resource)
    //         };
    //         deployStep.DependsOn(prepareStep);
    //         deployStep.DependsOn(WellKnownPipelineSteps.ProcessParameters);
    //         deployStep.RequiredBy(WellKnownPipelineSteps.Deploy);

    //         return [publishStep, prepareStep, deployStep];
    //     }));

    // }

    private static bool ShouldExcludeFromCompose(IResource resource)
        => resource.TryGetAnnotationsOfType<DokployDatabaseAnnotation>(out _)
           || resource.TryGetAnnotationsOfType<DokployExistingDatabaseAnnotation>(out _);

    /// <summary>
    /// Deploys to Dokploy: validates configuration, provisions native databases, and deploys each
    /// compute resource as a Dokploy application via the REST API.
    /// </summary>
    public static async Task DeployToDokployAsync(PipelineStepContext context, DokployDeploymentExecutor environment)
    {
        var ct = context.CancellationToken;

        var (serverUrlResolved, apiKeyResolved) = await ValidateDokployConfigurationAsync(
            context,
            environment,
            "Validating Dokploy configuration").ConfigureAwait(false);

        var applicationName = await environment.ResolveProjectNameAsync(ct).ConfigureAwait(false);
        var deploymentEnvironmentName = await environment.ResolveDeploymentEnvironmentNameAsync(ct).ConfigureAwait(false);

        using var client = new DokployApiClient(serverUrlResolved, apiKeyResolved);
        DokployOrganization? activeOrganization = null;
        try
        {
            activeOrganization = await client.GetActiveOrganizationAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Logger.LogDebug(ex, "Could not resolve active Dokploy organization.");
        }

        // Find or create the Dokploy project
        var project = await FindOrCreateProjectAsync(
            client,
            applicationName,
            deploymentEnvironmentName,
            activeOrganization,
            context.Logger,
            ct).ConfigureAwait(false);

        var projectEnvironment = await FindOrCreateEnvironmentAsync(
            client,
            project,
            deploymentEnvironmentName,
            context.Logger,
            ct).ConfigureAwait(false);

        var environmentId = projectEnvironment.EnvironmentId;

        context.Logger.LogInformation("Using Dokploy project '{ProjectName}' (ID: {ProjectId})", project.Name, project.ProjectId);

        if (activeOrganization is not null)
        {
            context.Logger.LogInformation(
                "Using Dokploy organization '{OrganizationName}' (ID: {OrganizationId})",
                activeOrganization.Name,
                activeOrganization.OrganizationId);
        }

        context.Logger.LogInformation(
            "Using Dokploy environment '{EnvironmentName}' (ID: {EnvironmentId})",
            projectEnvironment.Name,
            projectEnvironment.EnvironmentId);

        var computeResources = context.Model.Resources
            .Where(r => r is not DokployEnvironmentResource)
            .Where(r => !r.TryGetAnnotationsOfType<DokployDatabaseAnnotation>(out _))
            .Where(r => r is ProjectResource or ContainerResource or DockerComposeAspireDashboardResource)
            .Where(resource => environment.TryGetPublishedComposeService(resource, out _))
            .ToList();

        if (environment.Dashboard?.Resource is { } dashboardResource
            && !computeResources.Contains(dashboardResource)
            && environment.TryGetPublishedComposeService(dashboardResource, out _))
        {
            computeResources.Add(dashboardResource);
        }

        DokployAutoRegistry? autoRegistry = null;
        if (ShouldBootstrapProjectRegistry(environment, computeResources))
        {
            autoRegistry = await EnsureProjectRegistryAsync(
                context,
                client,
                project,
                projectEnvironment,
                serverUrlResolved,
                apiKeyResolved,
                ct).ConfigureAwait(false);

            await environment.PushApplicationImagesAsync(context, computeResources, autoRegistry, ct).ConfigureAwait(false);
        }

        // Provision Dokploy-native databases
        var databaseConnections = await ProvisionNativeDatabasesAsync(context, client, environmentId, ct).ConfigureAwait(false);

        // Build the resource → hostname mapping for env var resolution
        var hostnames = BuildHostnameMapping(context.Model, databaseConnections);
        var endpointPorts = BuildEndpointPortOverrides(databaseConnections);

        var configuredApplications = new List<(IResource Resource, DokployApplication Application)>(computeResources.Count);

        var appTask = await context.ReportingStep.CreateTaskAsync(
            $"Configuring {computeResources.Count} application(s) in Dokploy", ct).ConfigureAwait(false);

        await using (appTask.ConfigureAwait(false))
        {
            try
            {
                foreach (var resource in computeResources)
                {
                    var application = await EnsureApplicationShellAsync(
                        context,
                        client,
                        resource,
                        environmentId,
                        hostnames,
                        ct).ConfigureAwait(false);

                    configuredApplications.Add((resource, application));
                }

                foreach (var (resource, application) in configuredApplications)
                {
                    await ConfigureApplicationAsync(
                        environment,
                        context,
                        client,
                        resource,
                        application,
                        project.Name,
                        serverUrlResolved,
                        hostnames,
                        endpointPorts,
                        databaseConnections,
                        autoRegistry,
                        ct).ConfigureAwait(false);
                }

                await appTask.CompleteAsync(
                    $"Configured {computeResources.Count} application(s)",
                    CompletionState.Completed, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await appTask.CompleteAsync(
                    $"Application configuration failed: {ex.Message}",
                    CompletionState.CompletedWithError, ct).ConfigureAwait(false);
                throw;
            }
        }

        var deployTask = await context.ReportingStep.CreateTaskAsync(
            $"Deploying {configuredApplications.Count} application(s) to Dokploy", ct).ConfigureAwait(false);

        await using (deployTask.ConfigureAwait(false))
        {
            try
            {
                foreach (var (resource, application) in configuredApplications)
                {
                    await TriggerApplicationDeploymentAsync(context, client, resource, application, ct).ConfigureAwait(false);
                }

                await deployTask.CompleteAsync(
                    $"Deployed {configuredApplications.Count} application(s)",
                    CompletionState.Completed,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await deployTask.CompleteAsync(
                    $"Application deployment failed: {ex.Message}",
                    CompletionState.CompletedWithError,
                    ct).ConfigureAwait(false);
                throw;
            }
        }

        var applicationSummaryEntries = new List<(string ResourceName, string Links)>(configuredApplications.Count);

        foreach (var (resource, application) in configuredApplications)
        {
            if (GetManagedApplicationDomainEndpoint(resource) is null)
            {
                continue;
            }

            var domainHosts = await GetApplicationDomainHostsAsync(client, application.ApplicationId, ct).ConfigureAwait(false);
            var publicLinks = domainHosts
                .OrderBy(static host => host, StringComparer.OrdinalIgnoreCase)
                .Select(static host => $"https://{host}")
                .ToArray();

            applicationSummaryEntries.Add((
                resource.Name,
                publicLinks.Length > 0
                    ? string.Join(", ", publicLinks)
                    : $"Dokploy app: {application.AppName}"));
        }

        var databaseSummaryEntries = databaseConnections
            .OrderBy(static entry => entry.Key.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static entry => (
                ResourceName: entry.Key.Name,
                Endpoint: string.IsNullOrWhiteSpace(entry.Value.DatabaseName)
                    ? $"{entry.Value.Host}:{entry.Value.Port}"
                    : $"{entry.Value.Host}:{entry.Value.Port}/{entry.Value.DatabaseName}"))
            .ToArray();

        // Summary
        context.Summary.Add("🚀 Target", "Dokploy");
        context.Summary.Add("🌐 Server", serverUrlResolved);
        if (activeOrganization is not null)
        {
            context.Summary.Add("🏢 Organization", activeOrganization.Name);
        }
        context.Summary.Add("📦 Project", project.Name);
        context.Summary.Add("🧭 Environment", projectEnvironment.Name);
        foreach (var entry in applicationSummaryEntries)
        {
            context.Summary.Add($"🔗 {entry.ResourceName}", entry.Links);
        }
        if (autoRegistry is not null)
        {
            context.Summary.Add("📚 Registry", autoRegistry.RegistryHost);
        }
        if (databaseSummaryEntries.Length > 0)
        {
            foreach (var entry in databaseSummaryEntries)
            {
                context.Summary.Add($"🗃️ {entry.ResourceName}", entry.Endpoint);
            }
        }
    }

    private static async Task<(string ServerUrl, string ApiKey)> ValidateDokployConfigurationAsync(
        PipelineStepContext context,
        DokployDeploymentExecutor environment,
        string taskName)
    {
        var ct = context.CancellationToken;
        var validateTask = await context.ReportingStep.CreateTaskAsync(taskName, ct).ConfigureAwait(false);
        await using (validateTask.ConfigureAwait(false))
        {
            var serverUrl = await environment.ResolveServerUrlAsync(ct).ConfigureAwait(false);
            var apiKey = await environment.ResolveApiKeyAsync(ct).ConfigureAwait(false);
            string normalizedServerUrl;

            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                const string message = "Dokploy server URL was not provided. Supply it when prompted by aspire publish/deploy.";
                await validateTask.CompleteAsync(message, CompletionState.CompletedWithError, ct).ConfigureAwait(false);
                throw new InvalidOperationException(message);
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                const string message = "Dokploy API key was not provided. Supply it when prompted by aspire publish/deploy.";
                await validateTask.CompleteAsync(message, CompletionState.CompletedWithError, ct).ConfigureAwait(false);
                throw new InvalidOperationException(message);
            }

            try
            {
                normalizedServerUrl = DokployApiClient.NormalizeServerUrl(serverUrl);
            }
            catch (InvalidOperationException ex)
            {
                await validateTask.CompleteAsync(ex.Message, CompletionState.CompletedWithError, ct).ConfigureAwait(false);
                throw;
            }

            try
            {
                using var client = new DokployApiClient(normalizedServerUrl, apiKey);
                _ = await client.ListProjectsAsync(ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                var message =
                    $"Could not access Dokploy server '{normalizedServerUrl}'. If you entered only a host name, https:// is assumed automatically. If your Dokploy instance only responds over http:// or uses a different URL, update the server URL and try again. {ex.Message}";
                await validateTask.CompleteAsync(message, CompletionState.CompletedWithError, ct).ConfigureAwait(false);
                throw new InvalidOperationException(message, ex);
            }

            await validateTask.CompleteAsync(
                $"Configuration validated — server: {normalizedServerUrl}",
                CompletionState.Completed,
                ct).ConfigureAwait(false);

            return (normalizedServerUrl, apiKey);
        }
    }

    /// <summary>
    /// Deploys a single compute resource as a Dokploy application:
    /// 1. Create application shell
    /// 2. Set Docker image source via saveDockerProvider
    /// 3. Resolve and save environment variables
    /// 4. Trigger deployment
    /// </summary>
    private static async Task<DokployApplication> EnsureApplicationShellAsync(
        PipelineStepContext context,
        DokployApiClient client,
        IResource resource,
        string environmentId,
        Dictionary<IResource, string> hostnames,
        CancellationToken ct)
    {
        var appName = SanitizeName(resource.Name);
        var description = $"Provisioned from Aspire at {DateTime.UtcNow:O}";

        context.Logger.LogInformation("Deploying '{ResourceName}' as Dokploy application...", resource.Name);

        var existingApplications = await client.SearchApplicationsAsync(appName, environmentId, ct).ConfigureAwait(false);
        var app = ReuseLatest(
            existingApplications,
            environmentId,
            appName,
            application => application.Name,
            application => application.EnvironmentId,
            application => application.CreatedAt,
            context.Logger,
            "application");

        if (app is null)
        {
            app = await client.CreateApplicationAsync(
                appName, environmentId, appName: appName, description: description, ct: ct).ConfigureAwait(false);
            context.Logger.LogInformation("Created Dokploy application '{AppName}' (ID: {AppId})", app.AppName, app.ApplicationId);
        }
        else
        {
            context.Logger.LogInformation("Reusing Dokploy application '{AppName}' (ID: {AppId})", app.AppName, app.ApplicationId);
        }

        hostnames[resource] = app.AppName;
        return app;
    }

    private static async Task ConfigureApplicationAsync(
        DokployDeploymentExecutor environment,
        PipelineStepContext context,
        DokployApiClient client,
        IResource resource,
        DokployApplication app,
        string projectName,
        string serverUrl,
        Dictionary<IResource, string> hostnames,
        Dictionary<IResource, int> endpointPorts,
        Dictionary<IResource, DokployDatabaseConnection> databaseConnections,
        DokployAutoRegistry? autoRegistry,
        CancellationToken ct)
    {
        context.Logger.LogInformation("Configuring Dokploy application '{AppName}' for resource '{ResourceName}'...", app.AppName, resource.Name);

        // 2. Set Docker image source
        var dockerImage = await environment.ResolveApplicationDockerImageAsync(resource, autoRegistry, ct).ConfigureAwait(false);

        var usesProjectRegistry = autoRegistry is not null
            && dockerImage is not null
            && dockerImage.StartsWith($"{autoRegistry.RegistryHost}/", StringComparison.OrdinalIgnoreCase);

        if (dockerImage is not null)
        {
            await client.SaveDockerProviderAsync(
                app.ApplicationId, dockerImage,
                username: usesProjectRegistry ? autoRegistry?.Username : null,
                password: usesProjectRegistry ? autoRegistry?.Password : null,
                registryUrl: usesProjectRegistry ? autoRegistry?.RegistryUrl : null,
                ct: ct).ConfigureAwait(false);
            context.Logger.LogInformation("Set Docker image '{Image}' for application '{AppName}'", dockerImage, app.AppName);
        }

        if (usesProjectRegistry && autoRegistry?.RegistryId is not null)
        {
            var command = environment.GetApplicationCommand(resource);
            var args = environment.GetApplicationArgs(resource);
            await client.UpdateApplicationAsync(
                app.ApplicationId,
                registryId: autoRegistry.RegistryId,
                command: command,
                args: args,
                ct: ct).ConfigureAwait(false);
            context.Logger.LogInformation(
                "Linked Dokploy registry '{RegistryId}' to application '{AppName}'",
                autoRegistry.RegistryId,
                app.AppName);
        }
        else
        {
            var command = environment.GetApplicationCommand(resource);
            var args = environment.GetApplicationArgs(resource);
            if (command is not null || args is not null)
            {
                await client.UpdateApplicationAsync(
                    app.ApplicationId,
                    command: command,
                    args: args,
                    ct: ct).ConfigureAwait(false);
            }
        }

        // 3. Resolve and save environment variables
        context.Logger.LogInformation("Resolving environment variables for '{ResourceName}'...", resource.Name);
        var envVars = await ResolveEnvironmentVariablesAsync(resource, context, hostnames, endpointPorts, databaseConnections, ct).ConfigureAwait(false);
        context.Logger.LogInformation("Resolved {Count} environment variable(s) for '{ResourceName}'", envVars.Count, resource.Name);
        if (envVars.Count > 0)
        {
            var envString = string.Join("\n", envVars.Select(kv => $"{kv.Key}={kv.Value}"));
            await client.SaveApplicationEnvironmentAsync(app.ApplicationId, envString, ct: ct).ConfigureAwait(false);
            context.Logger.LogInformation("Saved {Count} env var(s) for application '{AppName}'", envVars.Count, app.AppName);
        }

        await SyncApplicationDomainsAsync(
            client,
            resource,
            app,
            projectName,
            serverUrl,
            endpointPorts,
            context.Logger,
            ct).ConfigureAwait(false);

        return;
    }

    private static async Task TriggerApplicationDeploymentAsync(
        PipelineStepContext context,
        DokployApiClient client,
        IResource resource,
        DokployApplication app,
        CancellationToken ct)
    {
        await client.DeployApplicationAsync(
            app.ApplicationId,
            title: $"Aspire deployment of {resource.Name}",
            description: $"Deployed from Aspire AppHost at {DateTime.UtcNow:O}",
            ct: ct).ConfigureAwait(false);
        context.Logger.LogInformation("Triggered deployment for application '{AppName}'", app.AppName);
    }

    private bool TryGetPublishedComposeService(IResource resource, out PublishedComposeServiceSnapshot service)
        => _publishedComposeServices.TryGetValue(SanitizeName(resource.Name), out service!);


    private async Task<string?> ResolveApplicationDockerImageAsync(
        IResource resource,
        DokployAutoRegistry? autoRegistry,
        CancellationToken ct)
    {
        string? publishedImage = null;
        string? localImageHint = null;

        if (TryGetPublishedComposeService(resource, out var publishedService))
        {
            publishedImage = publishedService.Image;
            localImageHint = string.IsNullOrWhiteSpace(publishedService.Image) || ContainsComposeVariable(publishedService.Image)
                ? publishedService.ServiceName
                : publishedService.Image;
        }

        if (autoRegistry is not null)
        {
            if (!string.IsNullOrWhiteSpace(publishedImage)
                && !ContainsComposeVariable(publishedImage))
            {
                var mirroredImage = await TryResolveProjectRegistryImageAsync(
                    localImageHint ?? publishedImage,
                    autoRegistry,
                    ct).ConfigureAwait(false);

                return mirroredImage ?? publishedImage;
            }

            var localImage = await ResolveLocalDockerImageAsync(
                localImageHint ?? GetContainerImage(resource) ?? resource.Name,
                ct).ConfigureAwait(false);
            return BuildProjectRegistryImage(localImage, autoRegistry);
        }

        if (!string.IsNullOrWhiteSpace(publishedImage) && !ContainsComposeVariable(publishedImage))
        {
            return publishedImage;
        }

        return GetContainerImage(resource);
    }

    private string? GetApplicationCommand(IResource resource)
        => TryGetPublishedComposeService(resource, out var publishedService) && publishedService.Entrypoint.Count > 0
            ? publishedService.Entrypoint[0]
            : null;

    private string[]? GetApplicationArgs(IResource resource)
    {
        if (!TryGetPublishedComposeService(resource, out var publishedService) || publishedService.Entrypoint.Count == 0)
        {
            return null;
        }

        var args = publishedService.Entrypoint.Skip(1)
            .Concat(publishedService.Command)
            .ToArray();
        return args.Length == 0 ? null : args;
    }

    private static bool ContainsComposeVariable(string image)
        => image.Contains("${", StringComparison.Ordinal);

    private static async Task<string?> TryResolveProjectRegistryImageAsync(
        string configuredImage,
        DokployAutoRegistry autoRegistry,
        CancellationToken ct)
    {
        var resolvedImage = await TryResolveLocalContainerImageAsync(configuredImage, ct).ConfigureAwait(false);
        return resolvedImage is null ? null : BuildProjectRegistryImage(resolvedImage.Image, autoRegistry);
    }

    private static async Task SyncApplicationDomainsAsync(
        DokployApiClient client,
        IResource resource,
        DokployApplication application,
        string projectName,
        string serverUrl,
        Dictionary<IResource, int> endpointPorts,
        ILogger logger,
        CancellationToken ct)
    {
        var endpoint = GetManagedApplicationDomainEndpoint(resource);
        if (endpoint is null)
        {
            await RemoveApplicationDomainsAsync(client, application.ApplicationId, logger, ct).ConfigureAwait(false);
            return;
        }

        var projectSlug = SanitizeName(projectName);
        var resourceSlug = SanitizeName(resource.Name);
        var preferredHost = DeriveApplicationHost(serverUrl, projectSlug, resourceSlug);
        var applicationHost = await CanResolveDnsAsync(preferredHost, ct).ConfigureAwait(false)
            ? preferredHost
            : await TryDeriveSslipApplicationHostAsync(serverUrl, projectSlug, resourceSlug, ct).ConfigureAwait(false) ?? preferredHost;

        var existingHosts = await GetApplicationDomainHostsAsync(client, application.ApplicationId, ct).ConfigureAwait(false);
        if (existingHosts.Contains(applicationHost, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var targetPort = GetEndpointPort(resource, endpoint.Name, endpointPorts);
        await client.CreateDomainAsync(
            applicationHost,
            port: targetPort,
            applicationId: application.ApplicationId,
            https: true,
            ct: ct).ConfigureAwait(false);
        logger.LogInformation(
            "Created application domain '{Host}' for '{AppName}' on port {Port}",
            applicationHost,
            application.AppName,
            targetPort);
    }

    private static EndpointAnnotation? GetManagedApplicationDomainEndpoint(IResource resource)
    {
        if (!resource.TryGetAnnotationsOfType<EndpointAnnotation>(out var endpoints))
        {
            return null;
        }

        var candidates = endpoints
            .Where(endpoint => string.Equals(endpoint.UriScheme, "http", StringComparison.OrdinalIgnoreCase)
                || string.Equals(endpoint.UriScheme, "https", StringComparison.OrdinalIgnoreCase))
            .Where(endpoint => resource is DockerComposeAspireDashboardResource || endpoint.IsExternal);

        return candidates
            .OrderBy(endpoint => string.Equals(endpoint.UriScheme, "http", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
    }

    private static async Task<HashSet<string>> GetApplicationDomainHostsAsync(
        DokployApiClient client,
        string applicationId,
        CancellationToken ct)
        => (await GetApplicationDomainsAsync(client, applicationId, ct).ConfigureAwait(false))
            .Select(static domain => domain.Host)
            .Where(static host => !string.IsNullOrWhiteSpace(host))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static async Task<DokployDomain[]> GetApplicationDomainsAsync(
        DokployApiClient client,
        string applicationId,
        CancellationToken ct)
    {
        try
        {
            using var document = await client.GetApplicationDomainsAsync(applicationId, ct).ConfigureAwait(false);
            var domains = new Dictionary<string, DokployDomain>(StringComparer.OrdinalIgnoreCase);
            CollectDomains(document.RootElement, domains);
            return domains.Values.ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static async Task RemoveApplicationDomainsAsync(
        DokployApiClient client,
        string applicationId,
        ILogger logger,
        CancellationToken ct)
    {
        var domains = await GetApplicationDomainsAsync(client, applicationId, ct).ConfigureAwait(false);
        foreach (var domain in domains)
        {
            if (string.IsNullOrWhiteSpace(domain.DomainId))
            {
                continue;
            }

            await client.DeleteDomainAsync(domain.DomainId, ct).ConfigureAwait(false);
            logger.LogInformation("Removed Dokploy domain '{Host}' (ID: {DomainId})", domain.Host, domain.DomainId);
        }
    }

    private static void CollectDomains(JsonElement element, Dictionary<string, DokployDomain> domains)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                string? domainId = null;
                string? host = null;

                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "domainId", StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        domainId = property.Value.GetString();
                    }
                    else if (string.Equals(property.Name, "host", StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        host = property.Value.GetString();
                    }
                }

                if (!string.IsNullOrWhiteSpace(domainId) && !string.IsNullOrWhiteSpace(host))
                {
                    domains[domainId] = new DokployDomain
                    {
                        DomainId = domainId,
                        Host = host
                    };
                }

                foreach (var property in element.EnumerateObject())
                {
                    CollectDomains(property.Value, domains);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectDomains(item, domains);
                }

                break;
        }
    }

    /// <summary>
    /// Gets the container image for a resource (from ContainerImageAnnotation).
    /// </summary>
    private static string? GetContainerImage(IResource resource)
    {
        if (resource.TryGetAnnotationsOfType<ContainerImageAnnotation>(out var imageAnnotations))
        {
            var imageAnnotation = imageAnnotations.LastOrDefault();
            if (imageAnnotation is not null)
            {
                // Include the registry if present
                var registry = imageAnnotation.Registry;
                var image = imageAnnotation.Image;
                var tag = imageAnnotation.Tag;

                var fullImage = string.IsNullOrEmpty(registry) ? image : $"{registry}/{image}";
                return string.IsNullOrEmpty(tag) ? fullImage : $"{fullImage}:{tag}";
            }
        }
        return null;
    }

    private static bool ShouldBootstrapProjectRegistry(
        DokployDeploymentExecutor environment,
        IEnumerable<IResource> computeResources)
    {
        if (environment.DefaultContainerRegistry is not null)
        {
            return false;
        }

        return computeResources.All(resource => !resource.TryGetAnnotationsOfType<ContainerRegistryReferenceAnnotation>(out _));
    }

    private static string BuildProjectRegistryImage(string localImage, DokployAutoRegistry autoRegistry)
    {
        var normalizedLocalImage = NormalizeDockerImageReference(localImage);
        var imagePart = normalizedLocalImage;
        string? tag = null;
        var tagSeparator = normalizedLocalImage.LastIndexOf(':');
        var pathSeparator = normalizedLocalImage.LastIndexOf('/');

        if (tagSeparator > pathSeparator)
        {
            imagePart = normalizedLocalImage[..tagSeparator];
            tag = normalizedLocalImage[(tagSeparator + 1)..];
        }

        var repositoryName = imagePart[(imagePart.LastIndexOf('/') + 1)..];
        var remoteImage = $"{autoRegistry.RegistryHost}/{autoRegistry.ImagePrefix}/{repositoryName}";
        return string.IsNullOrEmpty(tag) ? remoteImage : $"{remoteImage}:{tag}";
    }

    private static async Task<DokployAutoRegistry> EnsureProjectRegistryAsync(
        PipelineStepContext context,
        DokployApiClient client,
        DokployProject project,
        DokployProjectEnvironment projectEnvironment,
        string serverUrl,
        string apiKey,
        CancellationToken ct)
    {
        var projectSlug = SanitizeName(project.Name);
        var registryName = $"{projectSlug}-registry";
        var composeName = $"{projectSlug}-registry";
        var registryHost = await TryDeriveSslipRegistryHostAsync(serverUrl, projectSlug, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Could not derive an sslip.io registry host for Dokploy project '{project.Name}' from server '{serverUrl}'.");
        var username = projectSlug;
        var password = GenerateRegistryPassword(project.ProjectId, registryHost, apiKey);
        var htpasswdLine = $"{username}:{BCrypt.Net.BCrypt.HashPassword(password)}";
        var htpasswdPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(htpasswdLine));

        var registryTask = await context.ReportingStep.CreateTaskAsync(
            $"Bootstrapping project registry '{registryHost}'", ct).ConfigureAwait(false);
        await using (registryTask.ConfigureAwait(false))
        {
            try
            {
                var existingCompose = ReuseLatest(
                    await client.SearchComposesAsync(composeName, projectEnvironment.EnvironmentId, ct).ConfigureAwait(false),
                    projectEnvironment.EnvironmentId,
                    composeName,
                    compose => compose.Name,
                    compose => compose.EnvironmentId,
                    compose => compose.CreatedAt,
                    context.Logger,
                    "compose service");

                var compose = existingCompose ?? await client.CreateComposeAsync(
                    composeName,
                    projectEnvironment.EnvironmentId,
                    description: $"Private registry for Dokploy project {project.Name}",
                    ct: ct).ConfigureAwait(false);

                await client.UpdateComposeAsync(
                    compose.ComposeId,
                    BuildRegistryComposeFile(),
                    env: $"REGISTRY_AUTH_HTPASSWD_B64={htpasswdPayload}",
                    ct: ct).ConfigureAwait(false);

                if (existingCompose is null)
                {
                    context.Logger.LogInformation("Created Dokploy compose service '{ComposeName}' (ID: {ComposeId})", composeName, compose.ComposeId);
                }
                else
                {
                    context.Logger.LogInformation("Reusing Dokploy compose service '{ComposeName}' (ID: {ComposeId})", composeName, compose.ComposeId);
                }

                await client.DeployComposeAsync(compose.ComposeId, ct).ConfigureAwait(false);

                if (!await ComposeHasDomainAsync(client, compose.ComposeId, registryHost, ct).ConfigureAwait(false))
                {
                    try
                    {
                        await client.CreateComposeDomainAsync(
                            compose.ComposeId,
                            "registry",
                            registryHost,
                            5000,
                            https: true,
                            certificateType: "letsencrypt",
                            ct: ct).ConfigureAwait(false);
                        context.Logger.LogInformation(
                            "Requested Dokploy registry domain '{RegistryHost}' for compose '{ComposeName}'",
                            registryHost,
                            composeName);

                        await client.DeployComposeAsync(compose.ComposeId, ct).ConfigureAwait(false);
                        context.Logger.LogInformation(
                            "Redeployed compose '{ComposeName}' after adding registry domain '{RegistryHost}' so Traefik picks up the new route.",
                            composeName,
                            registryHost);
                    }
                    catch (Exception ex)
                    {
                        context.Logger.LogWarning(
                            ex,
                            "Failed to provision registry domain '{RegistryHost}'.",
                            registryHost);
                    }
                }
                else
                {
                    context.Logger.LogInformation(
                        "Reusing sslip.io registry host '{RegistryHost}' for compose '{ComposeName}'.",
                        registryHost,
                        composeName);
                }

                var registryUrl = registryHost;

                await WaitForRegistryCredentialsAsync(
                    registryHost,
                    username,
                    password,
                    ct).ConfigureAwait(false);

                var registries = await client.ListRegistriesAsync(ct).ConfigureAwait(false);
                var existingRegistry = registries.FirstOrDefault(registry =>
                    string.Equals(registry.RegistryUrl, registryUrl, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(registry.RegistryName, registryName, StringComparison.OrdinalIgnoreCase));

                if (existingRegistry is null)
                {
                    await client.CreateRegistryAsync(
                        registryName,
                        username,
                        password,
                        registryUrl,
                        imagePrefix: projectSlug,
                        ct: ct).ConfigureAwait(false);

                    registries = await client.ListRegistriesAsync(ct).ConfigureAwait(false);
                    existingRegistry = registries.FirstOrDefault(registry =>
                        string.Equals(registry.RegistryUrl, registryUrl, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(registry.RegistryName, registryName, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    var desiredImagePrefix = existingRegistry.ImagePrefix ?? projectSlug;
                    var registryMatches =
                        string.Equals(existingRegistry.RegistryName, registryName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(existingRegistry.RegistryUrl, registryUrl, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(existingRegistry.ImagePrefix ?? projectSlug, desiredImagePrefix, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(existingRegistry.Username ?? username, username, StringComparison.Ordinal)
                        && string.Equals(existingRegistry.Password ?? password, password, StringComparison.Ordinal);

                    if (registryMatches)
                    {
                        context.Logger.LogInformation(
                            "Reusing Dokploy registry '{RegistryName}' (ID: {RegistryId}) without update because the existing configuration already matches.",
                            existingRegistry.RegistryName,
                            existingRegistry.RegistryId);
                    }
                    else
                    {
                        await client.UpdateRegistryAsync(
                            existingRegistry.RegistryId,
                            registryName,
                            username,
                            password,
                            registryUrl,
                            imagePrefix: desiredImagePrefix,
                            ct: ct).ConfigureAwait(false);
                    }
                }

                await registryTask.CompleteAsync(
                    $"Registry ready at https://{registryHost}",
                    CompletionState.Completed,
                    ct).ConfigureAwait(false);

                return new DokployAutoRegistry(
                    registryName,
                    composeName,
                    registryHost,
                    registryUrl,
                    projectSlug,
                    username,
                    password,
                    htpasswdLine,
                    existingRegistry?.RegistryId,
                    compose.ComposeId);
            }
            catch (Exception ex)
            {
                await registryTask.CompleteAsync(
                    $"Project registry bootstrap failed: {ex.Message}",
                    CompletionState.CompletedWithError,
                    ct).ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task PushApplicationImagesAsync(
        PipelineStepContext context,
        IReadOnlyList<IResource> computeResources,
        DokployAutoRegistry autoRegistry,
        CancellationToken ct)
    {
        var images = new List<(IResource resource, ContainerCliTool containerCli, string localImage, string remoteImage)>();
        foreach (var resource in computeResources)
        {
            if (resource is DockerComposeAspireDashboardResource)
            {
                continue;
            }

            string? publishedImage = null;
            string? localImageHint = null;
            if (TryGetPublishedComposeService(resource, out var publishedService))
            {
                publishedImage = publishedService.Image;
                localImageHint = string.IsNullOrWhiteSpace(publishedService.Image) || ContainsComposeVariable(publishedService.Image)
                    ? publishedService.ServiceName
                    : publishedService.Image;
            }

            var configuredImage = localImageHint ?? GetContainerImage(resource);
            if (string.IsNullOrWhiteSpace(configuredImage))
            {
                continue;
            }

            ResolvedLocalContainerImage? resolvedImage;
            if (!string.IsNullOrWhiteSpace(publishedImage) && !ContainsComposeVariable(publishedImage))
            {
                resolvedImage = await TryResolveLocalContainerImageAsync(configuredImage, ct).ConfigureAwait(false);
                if (resolvedImage is null)
                {
                    continue;
                }
            }
            else
            {
                resolvedImage = await ResolveLocalContainerImageAsync(configuredImage, ct).ConfigureAwait(false);
            }

            images.Add((resource, resolvedImage.ContainerCli, resolvedImage.Image, BuildProjectRegistryImage(resolvedImage.Image, autoRegistry)));
        }

        if (images.Count == 0)
        {
            return;
        }

        var pushTask = await context.ReportingStep.CreateTaskAsync(
            $"Pushing {images.Count} image(s) to project registry", ct).ConfigureAwait(false);
        await using (pushTask.ConfigureAwait(false))
        {
            try
            {
                foreach (var runtimeImages in images.GroupBy(image => image.containerCli.FileName, StringComparer.OrdinalIgnoreCase))
                {
                    var containerCli = runtimeImages.First().containerCli;
                    await EnsureDockerRegistryLoginAsync(context.Logger, autoRegistry, containerCli, ct).ConfigureAwait(false);

                    foreach (var (resource, _, localImage, remoteImage) in runtimeImages)
                    {
                        await RunContainerCommandAsync(containerCli.FileName, "image", ["tag", localImage, remoteImage], ct).ConfigureAwait(false);
                        await RunContainerCommandAsync(containerCli.FileName, "image", ["push", remoteImage], ct).ConfigureAwait(false);
                        context.Logger.LogInformation(
                            "Pushed image for '{ResourceName}' to '{RemoteImage}' using {ContainerCli}",
                            resource.Name,
                            remoteImage,
                            containerCli.FileName);
                    }
                }

                await pushTask.CompleteAsync(
                    $"Pushed {images.Count} image(s) to {autoRegistry.RegistryHost}",
                    CompletionState.Completed,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await pushTask.CompleteAsync(
                    $"Image push failed: {ex.Message}",
                    CompletionState.CompletedWithError,
                    ct).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static async Task EnsureDockerRegistryLoginAsync(
        ILogger logger,
        DokployAutoRegistry autoRegistry,
        ContainerCliTool containerCli,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(s_registryBootstrapTimeout);

        (int ExitCode, string StandardOutput, string StandardError) lastResult = default;

        try
        {
            while (!timeoutCts.Token.IsCancellationRequested)
            {
                lastResult = await RunContainerCommandWithResultAsync(
                    containerCli.FileName,
                    "login",
                    [autoRegistry.RegistryHost, "--username", autoRegistry.Username, "--password-stdin"],
                    timeoutCts.Token,
                    autoRegistry.Password).ConfigureAwait(false);

                if (lastResult.ExitCode == 0)
                {
                    return;
                }

                logger.LogWarning(
                    "{ContainerCli} login to '{RegistryHost}' failed with exit code {ExitCode}. Retrying in {DelaySeconds}s.",
                    containerCli.FileName,
                    autoRegistry.RegistryHost,
                    lastResult.ExitCode,
                    s_registryProbeInterval.TotalSeconds);

                await Task.Delay(s_registryProbeInterval, timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }

        throw new InvalidOperationException(
            $"{containerCli.FileName} login failed with exit code {lastResult.ExitCode}: {lastResult.StandardError}{Environment.NewLine}{lastResult.StandardOutput}".Trim());
    }

    private static async Task RunContainerCommandAsync(
        string containerCli,
        string command,
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        string? standardInput = null)
    {
        var result = await RunContainerCommandWithResultAsync(containerCli, command, arguments, ct, standardInput).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{containerCli} {command} failed with exit code {result.ExitCode}: {result.StandardError}{Environment.NewLine}{result.StandardOutput}".Trim());
        }
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunContainerCommandWithResultAsync(
        string containerCli,
        string command,
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        string? standardInput = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = containerCli,
                UseShellExecute = false,
                RedirectStandardInput = standardInput is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.StartInfo.ArgumentList.Add(command);
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), ct).ConfigureAwait(false);
            await process.StandardInput.WriteLineAsync().ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return (process.ExitCode, stdout, stderr);
    }

    private static async Task<string> ResolveLocalDockerImageAsync(string configuredImage, CancellationToken ct)
        => (await ResolveLocalContainerImageAsync(configuredImage, ct).ConfigureAwait(false)).Image;

    private static async Task<ResolvedLocalContainerImage> ResolveLocalContainerImageAsync(string configuredImage, CancellationToken ct)
    {
        var failures = new List<string>();
        var resolvedImage = await TryResolveLocalContainerImageAsync(configuredImage, ct, failures).ConfigureAwait(false);
        if (resolvedImage is not null)
        {
            return resolvedImage;
        }

        var normalizedImage = NormalizeDockerImageReference(configuredImage);
        throw new InvalidOperationException(
            $"Could not locate local image '{normalizedImage}' in any supported container CLI. {string.Join(" ", failures)}".Trim());
    }

    private static async Task<ResolvedLocalContainerImage?> TryResolveLocalContainerImageAsync(
        string configuredImage,
        CancellationToken ct,
        List<string>? failures = null)
    {
        var normalizedImage = NormalizeDockerImageReference(configuredImage);
        var repositoryName = GetImageRepositoryName(normalizedImage);

        foreach (var containerCli in GetContainerCliCandidates())
        {
            var inspectResult = await TryRunContainerCommandWithResultAsync(
                containerCli.FileName,
                "image",
                ["inspect", normalizedImage],
                ct).ConfigureAwait(false);

            if (inspectResult is null)
            {
                failures?.Add($"{containerCli.FileName} is not available.");
                continue;
            }

            if (inspectResult.Value.ExitCode == 0)
            {
                return new ResolvedLocalContainerImage(containerCli, normalizedImage);
            }

            var listResult = await RunContainerCommandWithResultAsync(
                containerCli.FileName,
                "image",
                ["ls", repositoryName, "--format", "{{.Repository}}:{{.Tag}}"],
                ct).ConfigureAwait(false);

            var resolvedImage = listResult.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(static line => line.Trim())
                .FirstOrDefault(static line => !line.Contains("<none>", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(resolvedImage))
            {
                return new ResolvedLocalContainerImage(containerCli, resolvedImage);
            }

            failures?.Add($"{containerCli.FileName} inspect failed: {inspectResult.Value.StandardError}");
        }

        return null;
    }

    private static async Task<bool> DockerImageExistsAsync(string image, CancellationToken ct)
    {
        foreach (var containerCli in GetContainerCliCandidates())
        {
            var result = await TryRunContainerCommandWithResultAsync(
                containerCli.FileName,
                "image",
                ["inspect", image],
                ct).ConfigureAwait(false);

            if (result is { ExitCode: 0 })
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)?> TryRunContainerCommandWithResultAsync(
        string containerCli,
        string command,
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        string? standardInput = null)
    {
        try
        {
            return await RunContainerCommandWithResultAsync(containerCli, command, arguments, ct, standardInput).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static IEnumerable<ContainerCliTool> GetContainerCliCandidates()
    {
        yield return new ContainerCliTool("docker");
        yield return new ContainerCliTool("podman");
    }

    private static string NormalizeDockerImageReference(string image)
    {
        var imagePart = image;
        string? tag = null;
        var tagSeparator = image.LastIndexOf(':');
        var pathSeparator = image.LastIndexOf('/');

        if (tagSeparator > pathSeparator)
        {
            imagePart = image[..tagSeparator];
            tag = image[(tagSeparator + 1)..];
        }

        var normalizedImage = imagePart.ToLowerInvariant();
        return string.IsNullOrEmpty(tag) ? normalizedImage : $"{normalizedImage}:{tag}";
    }

    private static string GetImageRepositoryName(string image)
    {
        var normalizedImage = NormalizeDockerImageReference(image);
        var imagePart = normalizedImage;
        var tagSeparator = normalizedImage.LastIndexOf(':');
        var pathSeparator = normalizedImage.LastIndexOf('/');

        if (tagSeparator > pathSeparator)
        {
            imagePart = normalizedImage[..tagSeparator];
        }

        return imagePart[(imagePart.LastIndexOf('/') + 1)..];
    }

    private static string BuildRegistryComposeFile()
    {
        var builder = new StringBuilder();
        builder.AppendLine("services:");
        builder.AppendLine("  registry:");
        builder.AppendLine("    image: \"registry:2\"");
        builder.AppendLine("    restart: \"unless-stopped\"");
        builder.AppendLine("    expose:");
        builder.AppendLine("      - \"5000\"");
        builder.AppendLine("    environment:");
        builder.AppendLine("      REGISTRY_STORAGE_DELETE_ENABLED: \"true\"");
        builder.AppendLine("      REGISTRY_HTTP_ADDR: \"0.0.0.0:5000\"");
        builder.AppendLine("      REGISTRY_AUTH: \"htpasswd\"");
        builder.AppendLine("      REGISTRY_AUTH_HTPASSWD_REALM: \"Dokploy Registry\"");
        builder.AppendLine("      REGISTRY_AUTH_HTPASSWD_PATH: \"/auth/htpasswd\"");
        builder.AppendLine("    command:");
        builder.AppendLine("      - \"/bin/sh\"");
        builder.AppendLine("      - \"-c\"");
        builder.AppendLine("      - \"mkdir -p /auth && printf '%s' \\\"$REGISTRY_AUTH_HTPASSWD_B64\\\" | base64 -d > /auth/htpasswd && exec /entrypoint.sh /etc/docker/registry/config.yml\"");
        builder.AppendLine("    volumes:");
        builder.AppendLine("      - \"registry-data:/var/lib/registry\"");
        builder.AppendLine("volumes:");
        builder.AppendLine("  registry-data: {}");
        return builder.ToString();
    }

    private static async Task<bool> ComposeHasDomainAsync(
        DokployApiClient client,
        string composeId,
        string host,
        CancellationToken ct)
    {
        try
        {
            var hosts = await GetComposeDomainHostsAsync(client, composeId, ct).ConfigureAwait(false);
            return hosts.Contains(host, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<IReadOnlyList<string>> GetComposeDomainHostsAsync(
        DokployApiClient client,
        string composeId,
        CancellationToken ct)
    {
        using var document = await client.GetComposeAsync(composeId, ct).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("domains", out var domains) || domains.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return domains.EnumerateArray()
            .Select(domain => domain.TryGetProperty("host", out var hostElement) ? hostElement.GetString() : null)
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task WaitForRegistryCredentialsAsync(
        string registryHost,
        string username,
        string password,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(s_registryBootstrapTimeout);

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Aspire.Hosting.Dokploy", "0.1.0"));

        string? lastFailure = null;
        try
        {
            while (!timeoutCts.Token.IsCancellationRequested)
            {
                var probe = await ProbeRegistryCredentialsAsync(httpClient, registryHost, username, password, timeoutCts.Token).ConfigureAwait(false);
                if (probe.IsReady)
                {
                    return;
                }

                lastFailure = probe.Details;
                await Task.Delay(s_registryProbeInterval, timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }

        var diagnosticSuffix = string.IsNullOrWhiteSpace(lastFailure)
            ? "Initial sslip.io DNS and Let's Encrypt provisioning can take several minutes on a fresh registry domain."
            : $"Last probe result: {lastFailure} Initial sslip.io DNS and Let's Encrypt provisioning can take several minutes on a fresh registry domain.";

        throw new TimeoutException(
            $"Timed out after {s_registryBootstrapTimeout.TotalSeconds:0}s waiting for registry credentials to become valid at https://{registryHost}/v2/. {diagnosticSuffix}");
    }

    private static async Task<RegistryProbeResult> ProbeRegistryCredentialsAsync(
        HttpClient httpClient,
        string registryHost,
        string username,
        string password,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://{registryHost}/v2/"));
            var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);

            using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK
                ? RegistryProbeResult.Ready
                : new RegistryProbeResult(
                    false,
                    $"registry returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (SocketException ex)
        {
            return new RegistryProbeResult(false, $"socket error: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return new RegistryProbeResult(false, $"HTTP request failed: {ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new RegistryProbeResult(false, "HTTP request timed out.");
        }
        catch (TimeoutException ex)
        {
            return new RegistryProbeResult(false, $"timeout: {ex.Message}");
        }
    }

    private static async Task<bool> CanResolveDnsAsync(string host, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host).WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            return addresses.Length > 0;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static string DeriveApplicationHost(string serverUrl, string projectSlug, string resourceSlug)
    {
        var host = new Uri(serverUrl).Host;
        var hostParts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var baseDomain = hostParts.Length > 2 && hostParts[0] is "admin" or "panel" or "dokploy"
            ? string.Join('.', hostParts.Skip(1))
            : host;

        return $"{resourceSlug}-{projectSlug}.{baseDomain}";
    }

    private static async Task<string?> TryDeriveSslipRegistryHostAsync(string serverUrl, string projectSlug, CancellationToken ct)
    {
        try
        {
            var serverHost = new Uri(serverUrl).Host;
            var addresses = await Dns.GetHostAddressesAsync(serverHost).WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            var address = addresses.FirstOrDefault(static address => address.AddressFamily == AddressFamily.InterNetwork);
            return address is null
                ? null
                : $"container-registry-{projectSlug}.{address}.sslip.io";
        }
        catch (SocketException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static async Task<string?> TryDeriveSslipApplicationHostAsync(
        string serverUrl,
        string projectSlug,
        string resourceSlug,
        CancellationToken ct)
    {
        try
        {
            var serverHost = new Uri(serverUrl).Host;
            var addresses = await Dns.GetHostAddressesAsync(serverHost).WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            var address = addresses.FirstOrDefault(static address => address.AddressFamily == AddressFamily.InterNetwork);
            return address is null
                ? null
                : $"{resourceSlug}-{projectSlug}.{address}.sslip.io";
        }
        catch (SocketException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static string GenerateRegistryPassword(string projectId, string registryHost, string apiKey)
    {
        var keyBytes = Encoding.UTF8.GetBytes(apiKey);
        var payloadBytes = Encoding.UTF8.GetBytes($"{projectId}:{registryHost}");
        using var hmac = new HMACSHA256(keyBytes);
        return Convert.ToHexString(hmac.ComputeHash(payloadBytes)).ToLowerInvariant();
    }

    /// <summary>
    /// Resolves all environment variables for a resource by executing the environment callbacks,
    /// then structurally resolving each value using the Dokploy hostname mapping.
    /// This mirrors the upstream Docker Compose approach: instead of calling <c>GetValueAsync()</c>
    /// (which deadlocks on circular references), we walk the expression tree and substitute
    /// <see cref="EndpointReference"/>, <see cref="ConnectionStringReference"/>, <see cref="ReferenceExpression"/>,
    /// and <see cref="ParameterResource"/> values using the known Dokploy hostnames and ports.
    /// </summary>
    private static async Task<Dictionary<string, string>> ResolveEnvironmentVariablesAsync(
        IResource resource,
        PipelineStepContext context,
        Dictionary<IResource, string> hostnames,
        Dictionary<IResource, int> endpointPorts,
        Dictionary<IResource, DokployDatabaseConnection> databaseConnections,
        CancellationToken ct)
    {
        var result = new Dictionary<string, string>();

        if (!resource.TryGetAnnotationsOfType<EnvironmentCallbackAnnotation>(out var callbacks))
            return result;

        var envContext = new EnvironmentCallbackContext(context.ExecutionContext, resource);

        foreach (var callback in callbacks)
        {
            await callback.Callback(envContext).ConfigureAwait(false);
        }

        foreach (var (key, value) in envContext.EnvironmentVariables)
        {
            try
            {
                var resolved = await ResolveValueAsync(value, hostnames, endpointPorts, ct).ConfigureAwait(false);
                if (resolved is not null)
                {
                    result[key] = resolved;
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning(ex, "Failed to resolve env var '{Key}' for resource '{Resource}'", key, resource.Name);
            }
        }

        NormalizeResolvedEnvironmentVariables(result, databaseConnections);
        return result;
    }

    private static void NormalizeResolvedEnvironmentVariables(
        Dictionary<string, string> envVars,
        Dictionary<IResource, DokployDatabaseConnection> databaseConnections)
    {
        var serviceHttpUrls = envVars
            .Where(kv => kv.Key.StartsWith("services__", StringComparison.OrdinalIgnoreCase)
                && kv.Key.EndsWith("__http__0", StringComparison.OrdinalIgnoreCase))
            .Select(kv => new
            {
                ServiceName = kv.Key["services__".Length..^"__http__0".Length],
                kv.Value
            })
            .ToDictionary(entry => entry.ServiceName, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

        foreach (var key in envVars.Keys.ToArray())
        {
            if (!key.StartsWith("REVERSEPROXY__CLUSTERS__", StringComparison.OrdinalIgnoreCase)
                || !key.EndsWith("__ADDRESS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = CompositeServiceDiscoveryAddressRegex().Match(envVars[key]);
            if (!match.Success)
            {
                continue;
            }

            var serviceName = match.Groups["service"].Value;
            if (serviceHttpUrls.TryGetValue(serviceName, out var httpUrl))
            {
                envVars[key] = httpUrl;
            }
        }

        var databaseConnectionsByHost = new Dictionary<string, DokployDatabaseConnection>(StringComparer.OrdinalIgnoreCase);
        foreach (var (resource, connection) in databaseConnections)
        {
            if (!string.IsNullOrWhiteSpace(connection.Host))
            {
                databaseConnectionsByHost[connection.Host] = connection;
            }

            databaseConnectionsByHost[SanitizeName(resource.Name)] = connection;
            databaseConnectionsByHost[resource.Name] = connection;
        }

        foreach (var key in envVars.Keys.ToArray())
        {
            if (!key.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryBuildDokployConnectionString(envVars[key], databaseConnectionsByHost, out var rewritten))
            {
                continue;
            }

            envVars[key] = rewritten;
        }
    }

    /// <summary>
    /// Recursively resolves an environment variable value to a string using the Dokploy hostname
    /// mapping, mirroring how the upstream Docker Compose publisher resolves values structurally.
    /// </summary>
    private static async Task<string?> ResolveValueAsync(
        object value,
        Dictionary<IResource, string> hostnames,
        Dictionary<IResource, int> endpointPorts,
        CancellationToken ct)
    {
        // Recursive loop to handle unwrapping (ConnectionStringReference → expression → ...)
        while (true)
        {
            switch (value)
            {
                case string s:
                    return s;

                case EndpointReference ep:
                    {
                        var hostname = hostnames.GetValueOrDefault(ep.Resource) ?? ep.Resource.Name.ToLowerInvariant();
                        var port = GetEndpointPort(ep.Resource, ep.EndpointName, endpointPorts);
                        return $"{ep.Scheme}://{hostname}:{port}";
                    }

                case EndpointReferenceExpression epExpr:
                    {
                        var hostname = hostnames.GetValueOrDefault(epExpr.Endpoint.Resource)
                                       ?? epExpr.Endpoint.Resource.Name.ToLowerInvariant();
                        var port = GetEndpointPort(epExpr.Endpoint.Resource, epExpr.Endpoint.EndpointName, endpointPorts);
                        var scheme = epExpr.Endpoint.Scheme;

                        return epExpr.Property switch
                        {
                            EndpointProperty.Url => $"{scheme}://{hostname}:{port}",
                            EndpointProperty.Host or EndpointProperty.IPV4Host => hostname,
                            EndpointProperty.Port or EndpointProperty.TargetPort => port.ToString(),
                            EndpointProperty.HostAndPort => $"{hostname}:{port}",
                            EndpointProperty.Scheme => scheme,
                            _ => $"{scheme}://{hostname}:{port}"
                        };
                    }

                case ConnectionStringReference cs:
                    value = cs.Resource.ConnectionStringExpression;
                    continue;

                case IResourceWithConnectionString csrs:
                    value = csrs.ConnectionStringExpression;
                    continue;

                case ParameterResource param:
                    {
                        // Parameter values can be resolved directly — they don't depend on other resources
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(TimeSpan.FromSeconds(5));
                        return await param.GetValueAsync(cts.Token).ConfigureAwait(false);
                    }

                case ReferenceExpression expr:
                    {
                        if (expr is { Format: "{0}", ValueProviders.Count: 1 })
                        {
                            return await ResolveValueAsync(expr.ValueProviders[0], hostnames, endpointPorts, ct).ConfigureAwait(false);
                        }

                        var args = new object[expr.ValueProviders.Count];
                        for (var i = 0; i < expr.ValueProviders.Count; i++)
                        {
                            var val = await ResolveValueAsync(expr.ValueProviders[i], hostnames, endpointPorts, ct).ConfigureAwait(false);
                            args[i] = val ?? throw new InvalidOperationException($"Value provider at index {i} resolved to null");
                        }
                        return string.Format(CultureInfo.InvariantCulture, expr.Format, args);
                    }

                case IValueProvider provider:
                    {
                        // Fallback: try GetValueAsync with timeout
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(TimeSpan.FromSeconds(5));
                        return await provider.GetValueAsync(cts.Token).ConfigureAwait(false);
                    }

                case not null:
                    return value.ToString();

                default:
                    return null;
            }
        }
    }

    private static bool TryBuildDokployConnectionString(
        string connectionString,
        Dictionary<string, DokployDatabaseConnection> databaseConnectionsByHost,
        out string rewritten)
    {
        rewritten = connectionString;

        try
        {
            var builder = new DbConnectionStringBuilder
            {
                ConnectionString = connectionString
            };

            var host = GetConnectionStringValue(builder, "Host");
            if (string.IsNullOrWhiteSpace(host) || !databaseConnectionsByHost.TryGetValue(host, out var connection))
            {
                return false;
            }

            builder["Host"] = connection.Host;
            builder["Port"] = connection.Port;

            if (!string.IsNullOrWhiteSpace(connection.Username))
            {
                builder["Username"] = connection.Username;
            }

            if (!string.IsNullOrWhiteSpace(connection.Password))
            {
                builder["Password"] = connection.Password;
            }

            if (!string.IsNullOrWhiteSpace(connection.DatabaseName))
            {
                builder["Database"] = connection.DatabaseName;
            }

            rewritten = builder.ConnectionString;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? GetConnectionStringValue(DbConnectionStringBuilder builder, string key)
        => builder.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// Gets the target port for a named endpoint on a resource from its <see cref="EndpointAnnotation"/>.
    /// Falls back to common defaults if the annotation doesn't specify a target port.
    /// </summary>
    private static int GetEndpointPort(IResource resource, string endpointName, Dictionary<IResource, int> endpointPorts)
    {
        if (endpointPorts.TryGetValue(resource, out var overriddenPort))
        {
            return overriddenPort;
        }

        if (resource.TryGetAnnotationsOfType<EndpointAnnotation>(out var endpoints))
        {
            var endpoint = endpoints.FirstOrDefault(e => e.Name == endpointName);
            if (endpoint?.TargetPort is int port)
                return port;
            // If no explicit target port, use the endpoint's port
            if (endpoint?.Port is int allocatedPort)
                return allocatedPort;
        }

        // Sensible defaults for common schemes
        return endpointName switch
        {
            "https" => 443,
            "http" => 8080,
            _ => 8080
        };
    }

    /// <summary>
    /// Builds a mapping from each Aspire <see cref="IResource"/> to its Dokploy hostname
    /// (the Docker container name on the internal network). This mapping is used by
    /// <see cref="ResolveValueAsync"/> to substitute endpoint references with actual hostnames.
    /// </summary>
    private static Dictionary<IResource, string> BuildHostnameMapping(
        DistributedApplicationModel model,
        Dictionary<IResource, DokployDatabaseConnection> databaseConnections)
    {
        var mapping = new Dictionary<IResource, string>();

        foreach (var resource in model.Resources)
        {
            if (resource is DokployEnvironmentResource)
                continue;

            // Use the sanitized resource name as the Dokploy appName / Docker hostname
            mapping[resource] = databaseConnections.TryGetValue(resource, out var connection)
                ? connection.Host
                : SanitizeName(resource.Name);
        }

        return mapping;
    }

    /// <summary>
    /// Provisions Dokploy-native databases for resources annotated with <see cref="DokployDatabaseAnnotation"/>.
    /// Each annotated resource is created via the appropriate Dokploy API endpoint
    /// (e.g., <c>postgres.create</c>, <c>redis.create</c>, <c>mysql.create</c>, etc.).
    /// </summary>
    private static Dictionary<IResource, int> BuildEndpointPortOverrides(
        Dictionary<IResource, DokployDatabaseConnection> databaseConnections)
        => databaseConnections.ToDictionary(entry => entry.Key, entry => entry.Value.Port);

    private static async Task<Dictionary<IResource, DokployDatabaseConnection>> ProvisionNativeDatabasesAsync(
        PipelineStepContext context,
        DokployApiClient client,
        string environmentId,
        CancellationToken ct)
    {
        var dbResources = context.Model.Resources
            .Where(r => r.TryGetAnnotationsOfType<DokployDatabaseAnnotation>(out _))
            .ToList();

        if (dbResources.Count == 0)
        {
            return [];
        }

        var connections = new Dictionary<IResource, DokployDatabaseConnection>();

        var dbTask = await context.ReportingStep.CreateTaskAsync(
            $"Provisioning {dbResources.Count} Dokploy-native database(s)", ct).ConfigureAwait(false);

        await using (dbTask.ConfigureAwait(false))
        {
            try
            {
                foreach (var resource in dbResources)
                {
                    var annotation = resource.Annotations.OfType<DokployDatabaseAnnotation>().First();
                    var dbName = SanitizeName(resource.Name);
                    var description = $"Provisioned from Aspire at {DateTime.UtcNow:O}";

                    // Extract credentials from the Aspire resource's parameters
                    var (databaseUser, databasePassword, databaseName, dockerImage) =
                        await ExtractDatabaseCredentialsAsync(resource, ct).ConfigureAwait(false);

                    switch (annotation.DatabaseType)
                    {
                        case DokployDatabaseType.Postgres:
                            var postgresDefaults = CreateFallbackDatabaseConnection(
                                annotation.DatabaseType,
                                resource,
                                databaseName,
                                databaseUser,
                                databasePassword);
                            var existingPostgres = ReuseLatest(
                                await client.SearchPostgresAsync(dbName, environmentId, ct).ConfigureAwait(false),
                                environmentId,
                                dbName,
                                postgres => postgres.Name,
                                postgres => postgres.EnvironmentId,
                                postgres => postgres.CreatedAt,
                                context.Logger,
                                "PostgreSQL database");
                            var pg = existingPostgres is null
                                ? await client.CreatePostgresAsync(
                                    dbName, environmentId,
                                    appName: dbName,
                                    databaseName: databaseName,
                                    databaseUser: databaseUser,
                                    databasePassword: databasePassword,
                                    dockerImage: dockerImage,
                                    description: description,
                                    ct: ct).ConfigureAwait(false)
                                : await ReconcilePostgresConfigurationAsync(
                                    client,
                                    existingPostgres,
                                    dbName,
                                    environmentId,
                                    databaseName,
                                    databaseUser,
                                    databasePassword,
                                    dockerImage,
                                    description,
                                    ct).ConfigureAwait(false);
                            annotation.DokployDatabaseId = pg.PostgresId;
                            var postgresConnectionFallback = postgresDefaults with
                            {
                                Host = !string.IsNullOrWhiteSpace(pg.AppName)
                                    ? pg.AppName
                                    : postgresDefaults.Host
                            };
                            await EnsureDokployDatabaseDeploymentAsync(client, annotation, ct).ConfigureAwait(false);
                            connections[resource] = await WaitForDatabaseConnectionAsync(
                                client,
                                annotation,
                                postgresConnectionFallback,
                                ct).ConfigureAwait(false);
                            context.Logger.LogInformation(existingPostgres is null
                                ? "Provisioned Dokploy PostgreSQL '{Name}' (ID: {Id})"
                                : "Reusing Dokploy PostgreSQL '{Name}' (ID: {Id})", dbName, pg.PostgresId);
                            break;

                        case DokployDatabaseType.Redis:
                            var redisDefaults = CreateFallbackDatabaseConnection(
                                annotation.DatabaseType,
                                resource,
                                databaseName,
                                databaseUser,
                                databasePassword);
                            var existingRedis = ReuseLatest(
                                await client.SearchRedisAsync(dbName, environmentId, ct).ConfigureAwait(false),
                                environmentId,
                                dbName,
                                redis => redis.Name,
                                redis => redis.EnvironmentId,
                                redis => redis.CreatedAt,
                                context.Logger,
                                "Redis database");
                            var redis = existingRedis ?? await client.CreateRedisAsync(
                                dbName, environmentId,
                                appName: dbName,
                                databasePassword: databasePassword,
                                dockerImage: dockerImage,
                                 description: description, ct: ct).ConfigureAwait(false);
                            annotation.DokployDatabaseId = redis.RedisId;
                            await EnsureDokployDatabaseDeploymentAsync(client, annotation, ct).ConfigureAwait(false);
                            connections[resource] = await WaitForDatabaseConnectionAsync(
                                client,
                                annotation,
                                redisDefaults,
                                ct).ConfigureAwait(false);
                            context.Logger.LogInformation(existingRedis is null
                                ? "Provisioned Dokploy Redis '{Name}' (ID: {Id})"
                                : "Reusing Dokploy Redis '{Name}' (ID: {Id})", dbName, redis.RedisId);
                            break;

                        case DokployDatabaseType.MySql:
                            var mySqlDefaults = CreateFallbackDatabaseConnection(
                                annotation.DatabaseType,
                                resource,
                                databaseName,
                                databaseUser,
                                databasePassword);
                            var existingMySql = ReuseLatest(
                                await client.SearchMySqlAsync(dbName, environmentId, ct).ConfigureAwait(false),
                                environmentId,
                                dbName,
                                mysql => mysql.Name,
                                mysql => mysql.EnvironmentId,
                                mysql => mysql.CreatedAt,
                                context.Logger,
                                "MySQL database");
                            var mysql = existingMySql ?? await client.CreateMySqlAsync(
                                dbName, environmentId,
                                appName: dbName,
                                databaseName: databaseName,
                                databaseUser: databaseUser,
                                databasePassword: databasePassword,
                                databaseRootPassword: databasePassword,
                                dockerImage: dockerImage,
                                 description: description, ct: ct).ConfigureAwait(false);
                            annotation.DokployDatabaseId = mysql.MySqlId;
                            await EnsureDokployDatabaseDeploymentAsync(client, annotation, ct).ConfigureAwait(false);
                            connections[resource] = await WaitForDatabaseConnectionAsync(
                                client,
                                annotation,
                                mySqlDefaults,
                                ct).ConfigureAwait(false);
                            context.Logger.LogInformation(existingMySql is null
                                ? "Provisioned Dokploy MySQL '{Name}' (ID: {Id})"
                                : "Reusing Dokploy MySQL '{Name}' (ID: {Id})", dbName, mysql.MySqlId);
                            break;

                        case DokployDatabaseType.MariaDB:
                            var mariaDbDefaults = CreateFallbackDatabaseConnection(
                                annotation.DatabaseType,
                                resource,
                                databaseName,
                                databaseUser,
                                databasePassword);
                            var existingMariaDb = ReuseLatest(
                                await client.SearchMariaDBAsync(dbName, environmentId, ct).ConfigureAwait(false),
                                environmentId,
                                dbName,
                                mariadb => mariadb.Name,
                                mariadb => mariadb.EnvironmentId,
                                mariadb => mariadb.CreatedAt,
                                context.Logger,
                                "MariaDB database");
                            var mariadb = existingMariaDb ?? await client.CreateMariaDBAsync(
                                dbName, environmentId,
                                appName: dbName,
                                databaseName: databaseName,
                                databaseUser: databaseUser,
                                databasePassword: databasePassword,
                                databaseRootPassword: databasePassword,
                                dockerImage: dockerImage,
                                 description: description, ct: ct).ConfigureAwait(false);
                            annotation.DokployDatabaseId = mariadb.MariaDBId;
                            await EnsureDokployDatabaseDeploymentAsync(client, annotation, ct).ConfigureAwait(false);
                            connections[resource] = await WaitForDatabaseConnectionAsync(
                                client,
                                annotation,
                                mariaDbDefaults,
                                ct).ConfigureAwait(false);
                            context.Logger.LogInformation(existingMariaDb is null
                                ? "Provisioned Dokploy MariaDB '{Name}' (ID: {Id})"
                                : "Reusing Dokploy MariaDB '{Name}' (ID: {Id})", dbName, mariadb.MariaDBId);
                            break;

                        case DokployDatabaseType.MongoDB:
                            var mongoDefaults = CreateFallbackDatabaseConnection(
                                annotation.DatabaseType,
                                resource,
                                databaseName,
                                databaseUser,
                                databasePassword);
                            var existingMongo = ReuseLatest(
                                await client.SearchMongoAsync(dbName, environmentId, ct).ConfigureAwait(false),
                                environmentId,
                                dbName,
                                mongo => mongo.Name,
                                mongo => mongo.EnvironmentId,
                                mongo => mongo.CreatedAt,
                                context.Logger,
                                "MongoDB database");
                            var mongo = existingMongo ?? await client.CreateMongoAsync(
                                dbName, environmentId,
                                appName: dbName,
                                databaseUser: databaseUser,
                                databasePassword: databasePassword,
                                dockerImage: dockerImage,
                                 description: description, ct: ct).ConfigureAwait(false);
                            annotation.DokployDatabaseId = mongo.MongoId;
                            await EnsureDokployDatabaseDeploymentAsync(client, annotation, ct).ConfigureAwait(false);
                            connections[resource] = await WaitForDatabaseConnectionAsync(
                                client,
                                annotation,
                                mongoDefaults,
                                ct).ConfigureAwait(false);
                            context.Logger.LogInformation(existingMongo is null
                                ? "Provisioned Dokploy MongoDB '{Name}' (ID: {Id})"
                                : "Reusing Dokploy MongoDB '{Name}' (ID: {Id})", dbName, mongo.MongoId);
                            break;
                    }
                }

                await dbTask.CompleteAsync(
                    $"Provisioned {dbResources.Count} native database(s)",
                    CompletionState.Completed, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await dbTask.CompleteAsync(
                    $"Database provisioning failed: {ex.Message}",
                    CompletionState.CompletedWithError, ct).ConfigureAwait(false);
                throw;
            }
        }

        return connections;
    }

    private static async Task<DokployPostgres> ReconcilePostgresConfigurationAsync(
        DokployApiClient client,
        DokployPostgres postgres,
        string appName,
        string environmentId,
        string? databaseName,
        string? databaseUser,
        string? databasePassword,
        string? dockerImage,
        string? description,
        CancellationToken ct)
    {
        using var document = await client.GetPostgresAsync(postgres.PostgresId, ct).ConfigureAwait(false);
        var root = document.RootElement;

        var currentAppName = FindFirstString(root, "appName");
        var currentDatabaseName = FindFirstString(root, "databaseName", "dbName");
        var currentDatabaseUser = FindFirstString(root, "databaseUser", "username", "user");
        var currentDatabasePassword = FindFirstString(root, "databasePassword");
        var currentDockerImage = FindFirstString(root, "dockerImage");

        var requiresRecreate =
            !string.Equals(currentDatabaseName, databaseName, StringComparison.Ordinal)
            || !string.Equals(currentDatabaseUser, databaseUser, StringComparison.Ordinal)
            || !string.Equals(currentDatabasePassword, databasePassword, StringComparison.Ordinal);

        if (requiresRecreate)
        {
            await client.RemovePostgresAsync(postgres.PostgresId, ct).ConfigureAwait(false);
            await WaitForPostgresRemovalAsync(client, postgres.PostgresId, ct).ConfigureAwait(false);

            return await client.CreatePostgresAsync(
                postgres.Name,
                environmentId,
                appName: appName,
                databaseName: databaseName,
                databaseUser: databaseUser,
                databasePassword: databasePassword,
                dockerImage: dockerImage,
                description: description,
                ct: ct).ConfigureAwait(false);
        }

        if (string.Equals(currentAppName, appName, StringComparison.Ordinal)
            && string.Equals(currentDockerImage, dockerImage, StringComparison.Ordinal))
        {
            return postgres;
        }

        await client.UpdatePostgresAsync(
            postgres.PostgresId,
            appName: appName,
            dockerImage: dockerImage,
            ct: ct).ConfigureAwait(false);

        return postgres;
    }

    private static async Task WaitForPostgresRemovalAsync(
        DokployApiClient client,
        string postgresId,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(1));

        while (!timeoutCts.IsCancellationRequested)
        {
            try
            {
                using var _ = await client.GetPostgresAsync(postgresId, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException($"Timed out waiting for Dokploy PostgreSQL '{postgresId}' to be removed.");
    }

    private static DokployDatabaseConnection CreateFallbackDatabaseConnection(
        DokployDatabaseType databaseType,
        IResource resource,
        string? databaseName,
        string? databaseUser,
        string? databasePassword)
        => new(
            Host: SanitizeName(resource.Name),
            Port: databaseType switch
            {
                DokployDatabaseType.Postgres => 5432,
                DokployDatabaseType.Redis => 6379,
                DokployDatabaseType.MySql or DokployDatabaseType.MariaDB => 3306,
                DokployDatabaseType.MongoDB => 27017,
                _ => 0
            },
            DatabaseName: databaseName ?? resource.Name,
            Username: databaseUser,
            Password: databasePassword,
            ConnectionString: null);

    private static async Task<DokployDatabaseConnection> DiscoverDatabaseConnectionAsync(
        DokployApiClient client,
        DokployDatabaseAnnotation annotation,
        DokployDatabaseConnection fallback,
        CancellationToken ct)
    {
        var discovered = await TryReadDatabaseConnectionAsync(client, annotation, fallback, ct, ct).ConfigureAwait(false);
        return discovered ?? fallback;
    }

    private static async Task EnsureDokployDatabaseDeploymentAsync(
        DokployApiClient client,
        DokployDatabaseAnnotation annotation,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(annotation.DokployDatabaseId))
        {
            return;
        }

        switch (annotation.DatabaseType)
        {
            case DokployDatabaseType.Postgres:
                await client.DeployPostgresAsync(annotation.DokployDatabaseId, ct).ConfigureAwait(false);
                break;
            case DokployDatabaseType.Redis:
                await client.DeployRedisAsync(annotation.DokployDatabaseId, ct).ConfigureAwait(false);
                break;
            case DokployDatabaseType.MySql:
                await client.DeployMySqlAsync(annotation.DokployDatabaseId, ct).ConfigureAwait(false);
                break;
            case DokployDatabaseType.MariaDB:
                await client.DeployMariaDBAsync(annotation.DokployDatabaseId, ct).ConfigureAwait(false);
                break;
            case DokployDatabaseType.MongoDB:
                await client.DeployMongoAsync(annotation.DokployDatabaseId, ct).ConfigureAwait(false);
                break;
        }
    }

    private static async Task<DokployDatabaseConnection> WaitForDatabaseConnectionAsync(
        DokployApiClient client,
        DokployDatabaseAnnotation annotation,
        DokployDatabaseConnection fallback,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));

        while (!timeoutCts.Token.IsCancellationRequested)
        {
            var discovered = await TryReadDatabaseConnectionAsync(client, annotation, fallback, timeoutCts.Token, ct).ConfigureAwait(false);
            if (discovered is not null)
            {
                return discovered;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException($"Timed out waiting for Dokploy {annotation.DatabaseType} '{annotation.DokployDatabaseId}' to become readable.");
    }

    private static async Task<DokployDatabaseConnection?> TryReadDatabaseConnectionAsync(
        DokployApiClient client,
        DokployDatabaseAnnotation annotation,
        DokployDatabaseConnection fallback,
        CancellationToken ct,
        CancellationToken abortCt)
    {
        if (string.IsNullOrWhiteSpace(annotation.DokployDatabaseId))
        {
            return null;
        }

        try
        {
            using var document = annotation.DatabaseType switch
            {
                DokployDatabaseType.Postgres => await client.GetPostgresAsync(annotation.DokployDatabaseId, ct).ConfigureAwait(false),
                DokployDatabaseType.Redis => await client.GetRedisAsync(annotation.DokployDatabaseId, ct).ConfigureAwait(false),
                DokployDatabaseType.MySql => await client.GetMySqlAsync(annotation.DokployDatabaseId, ct).ConfigureAwait(false),
                DokployDatabaseType.MariaDB => await client.GetMariaDbAsync(annotation.DokployDatabaseId, ct).ConfigureAwait(false),
                DokployDatabaseType.MongoDB => await client.GetMongoAsync(annotation.DokployDatabaseId, ct).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unsupported Dokploy database type '{annotation.DatabaseType}'.")
            };

            var root = document.RootElement;
            var host = FindFirstString(root, "internalHost", "databaseHost", "host", "serviceName", "appName", "name") ?? fallback.Host;
            var port = FindFirstInt(root, "internalPort", "databasePort", "port", "externalPort") ?? fallback.Port;
            var databaseName = FindFirstString(root, "databaseName", "dbName") ?? fallback.DatabaseName;
            var username = FindFirstString(root, "databaseUser", "username", "user", "rootUser") ?? fallback.Username;
            var password = FindFirstString(root, "databasePassword", "password", "rootPassword") ?? fallback.Password;
            var connectionString = FindFirstString(root, "connectionString", "connectionUrl", "uri", "databaseUrl") ?? fallback.ConnectionString;

            return fallback with
            {
                Host = host,
                Port = port,
                DatabaseName = databaseName,
                Username = username,
                Password = password,
                ConnectionString = connectionString
            };
        }
        catch (OperationCanceledException) when (abortCt.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindFirstString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = FindFirstStringByName(element, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? FindFirstStringByName(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && string.Equals(propertyName, property.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value.GetString();
                }

                var nested = FindFirstStringByName(property.Value, propertyName);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindFirstStringByName(item, propertyName);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static int? FindFirstInt(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (propertyNames.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var number))
                    {
                        return number;
                    }

                    if (property.Value.ValueKind == JsonValueKind.String
                        && int.TryParse(property.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                    {
                        return number;
                    }
                }

                var nested = FindFirstInt(property.Value, propertyNames);
                if (nested.HasValue)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindFirstInt(item, propertyNames);
                if (nested.HasValue)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool ContainsPropertyValue(JsonElement element, string propertyName, string expectedValue)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && string.Equals(property.Value.GetString(), expectedValue, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (ContainsPropertyValue(property.Value, propertyName, expectedValue))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsPropertyValue(item, propertyName, expectedValue))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Extracts database credentials (username, password, database name, Docker image)
    /// from the Aspire resource's parameters. These are used to customize the Dokploy-native
    /// database provisioning. Values are resolved from <see cref="ParameterResource"/> instances
    /// configured via <c>.WithUserName()</c>, <c>.WithPassword()</c>, and container image annotations.
    /// </summary>
    /// <remarks>
    /// The Dokploy API requires <c>databaseName</c>, <c>databaseUser</c>, and <c>databasePassword</c>
    /// to be non-empty strings for PostgreSQL, MySQL, MariaDB, and MongoDB. When Aspire doesn't provide
    /// custom values, sensible defaults are used (e.g., resource name as database/user name, "postgres"
    /// as user for PostgreSQL, auto-generated password).
    /// </remarks>
    /// <returns>
    /// A tuple of (databaseUser, databasePassword, databaseName, dockerImage). Required fields
    /// are populated with defaults when not explicitly configured on the resource.
    /// </returns>
    private static async Task<(string? databaseUser, string? databasePassword, string? databaseName, string? dockerImage)>
        ExtractDatabaseCredentialsAsync(IResource resource, CancellationToken ct)
    {
        string? databaseUser = null;
        string? databasePassword = null;
        string? databaseName = null;
        string? dockerImage = null;

        // Extract Docker image from ContainerImageAnnotation
        if (resource.TryGetAnnotationsOfType<ContainerImageAnnotation>(out var imageAnnotations))
        {
            var imageAnnotation = imageAnnotations.LastOrDefault();
            if (imageAnnotation is not null)
            {
                dockerImage = string.IsNullOrEmpty(imageAnnotation.Tag)
                    ? imageAnnotation.Image
                    : $"{imageAnnotation.Image}:{imageAnnotation.Tag}";
            }
        }

        // Extract credentials from specific resource types
        switch (resource)
        {
            case PostgresServerResource postgres:
                if (postgres.UserNameParameter is { } pgUserParam)
                {
                    databaseUser = await pgUserParam.GetValueAsync(ct).ConfigureAwait(false);
                }
                databaseUser ??= "postgres";

                if (postgres.PasswordParameter is { } pgPasswordParam)
                {
                    var raw = await pgPasswordParam.GetValueAsync(ct).ConfigureAwait(false);
                    if (raw is not null) databasePassword = SanitizeDokployPassword(raw);
                }
                databasePassword ??= GenerateDefaultPassword();

                databaseName = postgres.Databases
                    .Select(static entry => entry.Value)
                    .FirstOrDefault(static name => !string.IsNullOrWhiteSpace(name))
                    ?? resource.Name;

                break;

            case RedisResource redis:
                if (redis.PasswordParameter is { } redisPasswordParam)
                {
                    var raw = await redisPasswordParam.GetValueAsync(ct).ConfigureAwait(false);
                    if (raw is not null) databasePassword = SanitizeDokployPassword(raw);
                }

                break;

            case MySqlServerResource mysql:
                if (mysql.PasswordParameter is { } mysqlPasswordParam)
                {
                    var raw = await mysqlPasswordParam.GetValueAsync(ct).ConfigureAwait(false);
                    if (raw is not null) databasePassword = SanitizeDokployPassword(raw);
                }
                databasePassword ??= GenerateDefaultPassword();

                databaseName = resource.Name;
                databaseUser ??= databaseName;

                break;

            case MongoDBServerResource mongo:
                if (mongo.UserNameParameter is { } mongoUserParam)
                {
                    databaseUser = await mongoUserParam.GetValueAsync(ct).ConfigureAwait(false);
                }
                databaseUser ??= resource.Name;

                if (mongo.PasswordParameter is { } mongoPasswordParam)
                {
                    var raw = await mongoPasswordParam.GetValueAsync(ct).ConfigureAwait(false);
                    if (raw is not null) databasePassword = SanitizeDokployPassword(raw);
                }
                databasePassword ??= GenerateDefaultPassword();

                break;
        }

        return (databaseUser, databasePassword, databaseName, dockerImage);
    }

    /// <summary>
    /// Dokploy's allowed password regex: <c>/^[a-zA-Z0-9@#%^&amp;*()_+\-=[\]{}|;:,.&lt;&gt;?~`]*$/</c>.
    /// Characters NOT in this set (e.g. <c>$ ! ' " \ / space</c>) are stripped.
    /// </summary>
    [GeneratedRegex(@"[^a-zA-Z0-9@#%\^&\*\(\)_\+\-=\[\]\{\}\|;:,\.\<\>\?~`]")]
    private static partial Regex DokployPasswordInvalidCharsRegex();

    [GeneratedRegex(@"^(?:https\+http|http\+https)://(?<service>[^/:]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex CompositeServiceDiscoveryAddressRegex();

    /// <summary>
    /// Sanitizes a password so it conforms to Dokploy's validation rules.
    /// Strips any characters not in the allowed set. If the result is too short
    /// (less than 8 chars), appends a generated suffix to ensure security.
    /// </summary>
    private static string SanitizeDokployPassword(string password)
    {
        var sanitized = DokployPasswordInvalidCharsRegex().Replace(password, "");
        if (sanitized.Length >= 8)
            return sanitized;

        // Stripped too many chars — pad with safe random characters
        return sanitized + GenerateDefaultPassword()[..(8 - sanitized.Length)];
    }

    /// <summary>
    /// Generates a random password for database provisioning when no custom password is configured.
    /// Uses a character set compatible with Dokploy's password validation rules.
    /// </summary>
    private static string GenerateDefaultPassword()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var result = new char[24];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = chars[System.Security.Cryptography.RandomNumberGenerator.GetInt32(chars.Length)];
        }
        return new string(result);
    }

    /// <summary>
    /// Converts a resource name to a Docker Compose–safe service name
    /// (lowercase, alphanumeric + hyphens, no leading/trailing hyphens).
    /// </summary>
    private static string SanitizeName(string name)
    {
        var lowered = name.ToLowerInvariant();
        var chars = new char[lowered.Length];
        var length = 0;
        var lastWasHyphen = true;

        foreach (var c in lowered)
        {
            if (char.IsLetterOrDigit(c))
            {
                chars[length++] = c;
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen)
            {
                chars[length++] = '-';
                lastWasHyphen = true;
            }
        }

        if (length > 0 && chars[length - 1] == '-')
        {
            length--;
        }

        return length > 0 ? new string(chars, 0, length) : "service";
    }

    private static string NormalizeDokployEnvironmentName(string? environmentName)
    {
        if (string.IsNullOrWhiteSpace(environmentName))
        {
            return DefaultDeploymentEnvironmentName;
        }

        return environmentName.Trim().ToLowerInvariant();
    }

    private static T? ReuseLatest<T>(
        IEnumerable<T> resources,
        string environmentId,
        string name,
        Func<T, string> getName,
        Func<T, string?> getEnvironmentId,
        Func<T, DateTimeOffset?> getCreatedAt,
        ILogger logger,
        string resourceType)
        where T : class
    {
        var matches = resources
            .Where(resource => string.Equals(getEnvironmentId(resource), environmentId, StringComparison.Ordinal))
            .Where(resource => string.Equals(getName(resource), name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(resource => getCreatedAt(resource) ?? DateTimeOffset.MinValue)
            .ToList();

        if (matches.Count > 1)
        {
            logger.LogWarning(
                "Found {Count} Dokploy {ResourceType} entries for '{Name}' in environment '{EnvironmentId}'. Reusing the newest entry.",
                matches.Count,
                resourceType,
                name,
                environmentId);
        }

        return matches.FirstOrDefault();
    }

    private sealed record PublishedComposeServiceSnapshot(
        string ServiceName,
        string? Image,
        IReadOnlyList<string> Entrypoint,
        IReadOnlyList<string> Command);

    private sealed record ContainerCliTool(string FileName);

    private sealed record ResolvedLocalContainerImage(ContainerCliTool ContainerCli, string Image);

    private sealed record RegistryProbeResult(bool IsReady, string? Details)
    {
        public static RegistryProbeResult Ready { get; } = new(true, null);
    }

    private static async Task<DokployProject> FindOrCreateProjectAsync(
        DokployApiClient client,
        string projectName,
        string environmentName,
        DokployOrganization? activeOrganization,
        ILogger logger,
        CancellationToken ct)
    {
        var projects = await client.ListProjectsAsync(ct).ConfigureAwait(false);
        var matches = projects
            .Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        DokployProject? existing = null;
        if (activeOrganization is not null)
        {
            existing = matches.FirstOrDefault(p =>
                string.Equals(p.OrganizationId, activeOrganization.OrganizationId, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            existing = matches.FirstOrDefault();
        }

        if (existing is not null)
        {
            logger.LogInformation(
                "Found existing Dokploy project '{ProjectName}'{OrganizationSuffix}",
                existing.Name,
                activeOrganization is null ? string.Empty : $" in organization '{activeOrganization.Name}'");
            return existing;
        }

        if (matches.Length > 0 && activeOrganization is null)
        {
            var organizationIds = matches
                .Select(static project => project.OrganizationId)
                .Where(static organizationId => !string.IsNullOrWhiteSpace(organizationId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (organizationIds.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Found multiple Dokploy projects named '{projectName}' across organizations, but the active organization could not be resolved. Set the target organization as active in Dokploy and retry.");
            }
        }

        logger.LogInformation("Creating new Dokploy project '{ProjectName}'", projectName);
        return await client.CreateProjectAsync(
            projectName,
            description: "Deployed from .NET Aspire",
            environmentName: environmentName,
            ct: ct).ConfigureAwait(false);
    }

    private static async Task<DokployProjectEnvironment> FindOrCreateEnvironmentAsync(
        DokployApiClient client,
        DokployProject project,
        string environmentName,
        ILogger logger,
        CancellationToken ct)
    {
        if (project.Environments is { Length: > 0 })
        {
            var existing = project.Environments.FirstOrDefault(e =>
                NormalizeDokployEnvironmentName(e.Name) == environmentName);

            if (existing is not null)
            {
                logger.LogInformation(
                    "Found existing Dokploy environment '{EnvironmentName}' in project '{ProjectName}'",
                    existing.Name,
                    project.Name);
                return existing;
            }
        }

        logger.LogInformation(
            "Creating Dokploy environment '{EnvironmentName}' in project '{ProjectName}'",
            environmentName,
            project.Name);

        return await client.CreateEnvironmentAsync(
            environmentName,
            project.ProjectId,
            description: $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(environmentName)} environment",
            ct: ct).ConfigureAwait(false);
    }
}
