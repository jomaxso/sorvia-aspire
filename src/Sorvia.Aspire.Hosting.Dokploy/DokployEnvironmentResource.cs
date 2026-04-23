using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Dokploy;

#pragma warning disable ASPIREATS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

[AspireExport(ExposeProperties = true)]
public class DokployEnvironmentResource : Resource, IComputeEnvironmentResource
{
    private const string DokployTag = "dokploy";
    private const string DokployDeployTag = "dokploy-deploy";

    public bool DashboardEnabled { get; set; } = true;

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
    /// The parameter resource providing the Dokploy environment name.
    /// When set, takes precedence over the <see cref="DeploymentEnvironmentName"/> string value.
    /// </summary>
    public ParameterResource? DeploymentEnvironmentNameParameter { get; set; }


    public IResourceBuilder<DokployAspireDashboardResource>? Dashboard { get; set; }

    public DokployEnvironmentResource(string name) : base(name)
    {
        Annotations.Add(new PipelineStepAnnotation(async context =>
        {
            var staps = await ConfigurePipelineStepsAsync(context);

            var dokployDeployStep = new PipelineStep
            {
                Name = $"dokploy-deploy-{Name}",
                Description = $"Deploy resources for environment {Name} using Dokploy",
                Tags = [DokployTag, DokployDeployTag],
                Action = ctx => Task.CompletedTask, // DokployDeploymentExecutor.DeployToDokployAsync(ctx, this),
                DependsOnSteps = [WellKnownPipelineSteps.ProcessParameters],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy],
            };

            return [.. staps, dokployDeployStep];
        }));

        // Add pipeline configuration annotation to wire up dependencies
        // This is where we wire up the build steps created by the resources
        Annotations.Add(new PipelineConfigurationAnnotation(context =>
        {
            ConfigurePipelineConfiguration(context);

            // This ensures that resources that have to be built before deployments are handled
            EnsureResourcesBuildBefore(context, $"dokploy-deploy-{Name}");

        }));
    }

    public async Task<IEnumerable<PipelineStep>> ConfigurePipelineStepsAsync(PipelineStepFactoryContext factoryContext)
    {
        var resources = GetResources(factoryContext.PipelineContext.Model);

        var steps = new List<PipelineStep>();

        foreach (var resource in resources)
        {
            var deploymentTarget = resource.GetDeploymentTargetAnnotation(this)?.DeploymentTarget;

            if (deploymentTarget is null || !deploymentTarget.TryGetAnnotationsOfType<PipelineStepAnnotation>(out var annotations))
            {
                continue;
            }

            foreach (var annotation in annotations)
            {
                var childFactoryContext = new PipelineStepFactoryContext
                {
                    PipelineContext = factoryContext.PipelineContext,
                    Resource = deploymentTarget
                };

                var deploymentTargetSteps = await annotation.CreateStepsAsync(childFactoryContext)
                    .ConfigureAwait(false);

                foreach (var step in deploymentTargetSteps)
                {
                    step.Resource ??= deploymentTarget;
                }

                steps.AddRange(deploymentTargetSteps);
            }
        }

        return steps;
    }

    private void ConfigurePipelineConfiguration(PipelineConfigurationContext context)
    {
        // Wire up build step dependencies for all compute resources (including dashboard if enabled)
        var resources = GetResources(context.Model);

        foreach (var resource in resources)
        {
            var deploymentTarget = resource.GetDeploymentTargetAnnotation(this)?.DeploymentTarget;

            if (deploymentTarget is null)
            {
                continue;
            }

            // Execute the PipelineConfigurationAnnotation callbacks on the deployment target
            if (deploymentTarget.TryGetAnnotationsOfType<PipelineConfigurationAnnotation>(out var annotations))
            {
                foreach (var annotation in annotations)
                {
                    annotation.Callback(context);
                }
            }

            // Ensure print-summary steps from deployment targets run after dokploy-deploy
            var dokployDeploySteps = context.GetSteps(this, DokployDeployTag);
            var printSummarySteps = context.GetSteps(deploymentTarget, "print-summary");
            printSummarySteps.DependsOn(dokployDeploySteps);
        }

        // This ensures that resources that have to be pushed before deployments are handled
        foreach (var pushResource in context.Model.GetBuildAndPushResources())
        {
            var pushSteps = context.GetSteps(pushResource, WellKnownPipelineTags.PushContainerImage);
            var dokployDeploySteps = context.GetSteps(this, DokployDeployTag);

            dokployDeploySteps.DependsOn(pushSteps);
        }
    }

    private IEnumerable<IResource> GetResources(DistributedApplicationModel model)
    {
        var resources = DashboardEnabled && Dashboard?.Resource is DokployAspireDashboardResource dashboard
            ? [.. model.GetComputeResources(), dashboard]
            : model.GetComputeResources();

        return resources;
    }

    private void EnsureResourcesBuildBefore(PipelineConfigurationContext context, string stepName)
    {
        foreach (var computeResource in context.Model.GetBuildResources())
        {
            var buildSteps = context.GetSteps(computeResource, WellKnownPipelineTags.BuildCompute);

            buildSteps.RequiredBy(WellKnownPipelineSteps.Deploy)
                    .RequiredBy(stepName)
                    .DependsOn(WellKnownPipelineSteps.DeployPrereq);
        }
    }
}