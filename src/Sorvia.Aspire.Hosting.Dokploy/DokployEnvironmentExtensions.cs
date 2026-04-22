#pragma warning disable ASPIREINTERACTION001 // This type is used for interaction with the Dokploy REST API and is not intended for direct use by application code. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREATS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Dokploy;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;

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
    private static readonly Type s_dockerComposeInfrastructureType =
        typeof(DockerComposeEnvironmentResource).Assembly.GetType("Aspire.Hosting.Docker.DockerComposeInfrastructure")
        ?? throw new InvalidOperationException("Could not find Docker compose infrastructure type.");

    private static readonly PropertyInfo s_dashboardProperty =
        typeof(DockerComposeEnvironmentResource).GetProperty("Dashboard", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Could not find Docker compose dashboard property.");

    private static readonly MethodInfo s_createDashboardMethod =
        typeof(DockerComposeAspireDashboardResourceBuilderExtensions).GetMethod(
            "CreateDashboard",
            BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Could not find Docker compose dashboard factory method.");

    private static IDistributedApplicationBuilder AddDokployDockerComposeInfrastructure(this IDistributedApplicationBuilder builder)
    {
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton(typeof(IDistributedApplicationEventingSubscriber), s_dockerComposeInfrastructureType));

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
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        builder.AddDokployDockerComposeInfrastructure();

        // Check if a Dokploy environment already exists (only one allowed)
        if (builder.Resources.OfType<DokployEnvironmentResource>().SingleOrDefault() is { } existingResource)
        {
            return builder.CreateResourceBuilder(existingResource);
        }

        var resource = new DokployEnvironmentResource(name)
        {
            DeploymentEnvironmentName = "production"
        };

        var dashboard = ((IResourceBuilder<DockerComposeAspireDashboardResource>)s_createDashboardMethod.Invoke(null, [builder, "aspire-dashboard"])!)
            .PublishAsDockerComposeService((_, service) =>
            {
                service.Restart = "always";
            });
        s_dashboardProperty.SetValue(resource, dashboard);

        if (builder.ExecutionContext.IsRunMode)
        {
            // In run mode, the Dokploy environment is not needed —
            // return a builder that isn't added to the application model
            return builder.CreateResourceBuilder(resource);
        }

        resource.ServerUrlParameter = builder.AddParameter("dokploy-url").Resource;
        resource.ApiKeyParameter = builder.AddParameter("dokploy-api-key", secret: true).Resource;
        resource.ProjectNameParameter = builder.AddParameter("dokploy-project-name").Resource;
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

        // In publish mode, add the resource to the application model
        // but exclude it from the manifest (it's not a traditional publishable resource).
        // Pipeline steps are registered via PipelineStepAnnotation in the constructor.
        return builder.AddResource(resource)
            .ExcludeFromManifest();
    }

    /// <summary>
    /// Enables or disables the Aspire Dashboard container for telemetry visualization
    /// in the deployed Docker Compose environment.
    /// </summary>
    /// <param name="builder">The Dokploy environment resource builder.</param>
    /// <param name="enabled">Whether to enable the dashboard. Default is <c>true</c>.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// When enabled, an Aspire Dashboard container (<c>mcr.microsoft.com/dotnet/aspire-dashboard</c>)
    /// is added to the generated Docker Compose file. All other services are automatically configured
    /// to send OpenTelemetry (OTLP) telemetry to the dashboard via the <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>
    /// environment variable.
    /// </para>
    /// <para>
    /// The dashboard is enabled by default, matching the behavior of
    /// <c>DockerComposeEnvironmentResource.WithDashboard()</c> in <c>Aspire.Hosting.Docker</c>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Dashboard enabled by default
    /// builder.AddDokployEnvironment("dokploy");
    ///
    /// // Explicitly disable the dashboard
    /// builder.AddDokployEnvironment("dokploy").WithDashboard(false);
    /// </code>
    /// </example>
    [AspireExport("withDashboard", Description = "Enables or disables the Aspire dashboard for the Dokploy environment")]
    public static IResourceBuilder<DokployEnvironmentResource> WithDashboard(
        this IResourceBuilder<DokployEnvironmentResource> builder,
        bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.DashboardEnabled = enabled;
        return builder;
    }

    /// <summary>
    /// Allows setting the properties of a Dokploy environment resource, including inherited Docker Compose settings.
    /// </summary>
    [AspireExportIgnore(Reason = "General-purpose configuration method for Dokploy environment resources. Not intended for direct use in most scenarios.")]
    public static IResourceBuilder<DokployEnvironmentResource> WithProperties(
        this IResourceBuilder<DokployEnvironmentResource> builder,
        Action<DokployEnvironmentResource> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        configure(builder.Resource);
        return builder;
    }

    /// <summary>
    /// Configures the Dokploy environment to use the specified container registry as the default
    /// for all resources that don't have an explicit <c>WithContainerRegistry</c> call.
    /// </summary>
    /// <param name="builder">The Dokploy environment resource builder.</param>
    /// <param name="registry">The container registry resource builder.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// When deploying to Dokploy, container images built from <c>ProjectResource</c> instances need to be
    /// pushed to a container registry so that the Dokploy server can pull them. Use this method to
    /// set a default registry for all project resources.
    /// </para>
    /// <para>
    /// To set a registry on individual resources instead, use the standard Aspire
    /// <c>WithContainerRegistry</c> extension method on each resource:
    /// </para>
    /// <code>
    /// var registry = builder.AddContainerRegistry("docker-hub", "docker.io", "myusername");
    /// builder.AddProject&lt;MyProject&gt;("myproject").WithContainerRegistry(registry);
    /// </code>
    /// </remarks>
    /// <example>
    /// <code>
    /// var registry = builder.AddContainerRegistry("ghcr", "ghcr.io", "myorg");
    ///
    /// builder.AddDokployEnvironment("dokploy")
    ///     .WithContainerRegistry(registry);
    /// </code>
    /// </example>
    [AspireExport("withContainerRegistry", Description = "Configures the Dokploy environment to use a default container registry")]
    public static IResourceBuilder<DokployEnvironmentResource> WithContainerRegistry<TContainerRegistry>(
        this IResourceBuilder<DokployEnvironmentResource> builder,
        IResourceBuilder<TContainerRegistry> registry)
        where TContainerRegistry : IResource, IContainerRegistry
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(registry);

        builder.Resource.DefaultContainerRegistry = registry.Resource;
        var existingAnnotation = builder.Resource.Annotations.OfType<ContainerRegistryReferenceAnnotation>().FirstOrDefault();
        if (existingAnnotation is not null)
        {
            builder.Resource.Annotations.Remove(existingAnnotation);
        }

        builder.Resource.Annotations.Add(new ContainerRegistryReferenceAnnotation(registry.Resource));
        return builder;
    }
}
