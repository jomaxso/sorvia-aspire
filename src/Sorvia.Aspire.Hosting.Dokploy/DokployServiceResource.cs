
#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dokploy;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal sealed class DokployServiceResource : Resource, IResourceWithParent<DokployEnvironmentResource>
{
    private readonly IResource _targetResource;
    private readonly DokployEnvironmentResource _composeEnvironmentResource;

    public DokployServiceResource(string name, IResource resource, DokployEnvironmentResource composeEnvironmentResource) : base(name)
    {
        _targetResource = resource;
        _composeEnvironmentResource = composeEnvironmentResource;

        // Add pipeline step annotation to display endpoints after deployment
        Annotations.Add(new PipelineStepAnnotation(_ =>
        {
            var steps = new List<PipelineStep>();

            var craeteAndStartRegristryStep = new PipelineStep
            {
                Name = $"create-and-start-registry-for-{Name}",
                Description = $"Create and start a container registry for {Name} if needed",
                Tags = ["dokploy", "registry-setup"],
                Action = ctx =>
                {
                    var registry = DokployDefaultContainerRegistry.Instance;

                    // registry.Endpoint = ;
                    // registry.Repository = ;

                    // No registry setup needed for Dokploy since we use a hosted registry in the Dokploy environment
                    return Task.CompletedTask;
                },
                RequiredBySteps = [WellKnownPipelineSteps.Deploy]
            };

            var printResourceSummary = new PipelineStep
            {
                Name = $"print-{_targetResource.Name}-summary",
                Action = async ctx => await PrintEndpointsAsync(ctx, _composeEnvironmentResource).ConfigureAwait(false),
                Tags = ["print-summary"],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy]
            };

            steps.Add(printResourceSummary);

            return steps;
        }));
    }

    internal record struct EndpointMapping(
        IResource Resource,
        string Scheme,
        string Host,
        string InternalPort,
        int? ExposedPort,
        bool IsExternal,
        string EndpointName);

    /// <summary>
    /// Gets the resource that is the target of this Docker Compose service.
    /// </summary>
    internal IResource TargetResource => _targetResource;

    /// <summary>
    /// Gets the mapping of endpoint names to their configurations.
    /// </summary>
    internal Dictionary<string, EndpointMapping> EndpointMappings { get; } = [];

    /// <inheritdoc/>
    public DokployEnvironmentResource Parent => _composeEnvironmentResource;

    private async Task PrintEndpointsAsync(PipelineStepContext context, DokployEnvironmentResource environment)
    {
        // No external endpoints configured - this is valid for internal-only services
        var externalEndpointMappings = EndpointMappings.Values
            .Where(m => m.IsExternal)
            .ToList();

        if (externalEndpointMappings.Count == 0)
        {
            context.ReportingStep.Log(LogLevel.Information,
                new MarkdownString($"Successfully deployed **{TargetResource.Name}** to Dokploy environment **{environment.Name}**. No public endpoints were configured."));
            context.Summary.Add(TargetResource.Name, "No public endpoints");
            return;
        }

        // TODO: Query the running containers for published ports
        HashSet<string> endpoints = [];

        if (endpoints.Count > 0)
        {
            var endpointList = string.Join(", ", endpoints.Select(e => $"[{e}]({e})"));
            context.ReportingStep.Log(LogLevel.Information, new MarkdownString($"Successfully deployed **{TargetResource.Name}** to {endpointList}."));
            context.Summary.Add(TargetResource.Name, string.Join(", ", endpoints));
        }
        else
        {
            // No published ports found in compose output.
            context.ReportingStep.Log(LogLevel.Information,
                new MarkdownString($"Successfully deployed **{TargetResource.Name}** to Dokploy environment **{environment.Name}**."));
            context.Summary.Add(TargetResource.Name, "No public endpoints");
        }
    }
}
