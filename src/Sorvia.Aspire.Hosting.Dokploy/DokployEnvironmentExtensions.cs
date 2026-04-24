#pragma warning disable ASPIREINTERACTION001 // This type is used for interaction with the Dokploy REST API and is not intended for direct use by application code. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREATS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dokploy;
using Aspire.Hosting.Lifecycle;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding Dokploy deployment support to an Aspire distributed application.
/// </summary>
/// <remarks>
/// Dokploy (https://dokploy.com) is a free, self-hostable PaaS that simplifies deployment
/// and management of applications and databases. This integration enables deploying an entire
/// Aspire application to a Dokploy instance with a single method call.
///
/// <para><b>How it works:</b></para>
/// <list type="number">
///   <item><description>
///     <see cref="AddDokployEnvironment"/> registers a <see cref="DokployEnvironmentResource"/>
///     as the deployment target in the Aspire model.
///   </description></item>
///   <item><description>
///     When the AppHost runs in publish mode, the resource reuses Aspire.Hosting.Docker's
///     Docker Compose publish and prepare behavior.
///   </description></item>
///   <item><description>
///     The deploy step validates Dokploy configuration, provisions Dokploy-native databases,
///     and deploys application resources to Dokploy via the REST API.
///   </description></item>
/// </list>
///
/// <para><b>Pipeline steps:</b></para>
/// <para>
/// The resource follows the Docker Compose pipeline shape but swaps the deploy behavior:
/// </para>
/// <list type="bullet">
///   <item><description><c>publish-{name}</c> — Runs the exact Aspire.Hosting.Docker publish implementation. RequiredBy <c>Publish</c>.</description></item>
///   <item><description><c>prepare-{name}</c> — Runs the exact Aspire.Hosting.Docker prepare implementation before deployment.</description></item>
///   <item><description><c>deploy-{name}</c> — Validates Dokploy configuration and deploys resources to Dokploy. DependsOn <c>prepare-{name}</c>, RequiredBy <c>Deploy</c>.</description></item>
/// </list>
///
/// <para><b>Configuration:</b></para>
/// <para>
/// The Dokploy server URL, API key, project name, and deployment environment are captured
/// as Aspire parameters when <c>aspire deploy</c> needs them. Plain <c>aspire publish</c>
/// can still generate Docker Compose artifacts without Dokploy credentials.
/// </para>
/// </remarks>
public static class DokployEnvironmentExtensions
{
    internal static IDistributedApplicationBuilder AddDokployInfrastructureCore(this IDistributedApplicationBuilder builder)
    {
        builder.Services.TryAddEventingSubscriber<DokployInfrastructure>();

        return builder;
    }

    /// <summary>
    /// Adds a Dokploy deployment environment to the Aspire application model.
    /// When the AppHost is published, all resources are automatically deployed
    /// to the configured Dokploy instance using Docker-backed publish artifacts and Dokploy deployment steps.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">
    /// A logical name for the Dokploy environment resource (e.g., "dokploy", "staging", "production").
    /// This name is used in the Aspire resource model and dashboard.
    /// </param>
    /// <returns>
    /// An <see cref="IResourceBuilder{T}"/> for further configuration
    /// via methods such as <see cref="WithServerId"/>, <see cref="WithDashboard(bool)"/>,
    /// and <see cref="WithContainerRegistry"/>.
    /// </returns>
    /// <example>
    /// <code>
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// // Add Dokploy as the deployment target. The first deploy will prompt
    /// // for the Dokploy server URL, API key, project name, and environment.
    /// builder.AddDokployEnvironment("my-roadmap");
    ///
    /// // ... add other resources ...
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport("addDokployEnvironment", Description = "Adds a Dokploy publishing environment")]
    public static IResourceBuilder<DokployEnvironmentResource> AddDokployEnvironment(
        this IDistributedApplicationBuilder builder,
        string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddDokployInfrastructureCore();

        var resource = new DokployEnvironmentResource(name)
        {
            // Initialize the dashboard resource
            Dashboard = builder.CreateAspireDashboard($"{name}-dashboard")
        };

        if (builder.ExecutionContext.IsRunMode)
        {
            // Return a builder that isn't added to the top-level application builder
            // so it doesn't surface as a resource.
            return builder.CreateResourceBuilder(resource);
        }

        resource.ServerUrlParameter = builder.AddParameter("dokploy-url")
            .WithDescription("URL of the Dokploy server to deploy to.")
            .Resource;

        resource.ApiKeyParameter = builder.AddParameter("dokploy-api-key", secret: true)
            .WithDescription("API key for authenticating with the Dokploy server.")
            .Resource;

        resource.ProjectNameParameter = builder.AddParameter("dokploy-project-name")
            .WithDescription("Target Dokploy project name. Leave empty to use the environment name.")
            .WithCustomInput(parameter => new()
            {
                Name = parameter.Name,
                Label = "Dokploy project name",
                Description = parameter.Description,
                InputType = InputType.Text,
                Placeholder = name,
                Value = name,
                Required = true
            })
            .Resource;

        resource.DeploymentEnvironmentNameParameter = builder.AddParameter("dokploy-environment")
            .WithDescription("Target Dokploy environment inside the project. Leave empty to use production.")
            .WithCustomInput(parameter => new()
            {
                Name = parameter.Name,
                Label = "Dokploy environment",
                Description = parameter.Description,
                InputType = InputType.Text,
                Placeholder = "production",
                Value = "production",
                Required = false
            })
            .Resource;

        return builder.AddResource(resource);
    }

    internal static IResourceBuilder<DokployAspireDashboardResource> CreateAspireDashboard(
        this IDistributedApplicationBuilder builder,
        string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = new DokployAspireDashboardResource(name);

        // Initialize the dashboard resource
        return builder.CreateResourceBuilder(resource)
                      .WithImage("mcr.microsoft.com/dotnet/nightly/aspire-dashboard")
                      .WithHttpEndpoint(targetPort: 18888)
                      // Expose the HTTP endpoint externally for the dashboard, it is password protected
                      // and disabled by default so an explicit call is required to turn it on.
                      .WithEndpoint(endpointName: "http", e => e.IsExternal = true)
                      .WithHttpEndpoint(name: "otlp-grpc", targetPort: 18889)
                      .WithHttpEndpoint(name: "otlp-http", targetPort: 18890);
    }
}