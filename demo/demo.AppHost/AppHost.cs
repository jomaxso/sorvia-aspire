#pragma warning disable ASPIRECSHARPAPPS001
#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.Docker;
using Aspire.Hosting.Pipelines;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("demo")
    .WithDokployDeploymentTarget();

var server = builder.AddCSharpApp("server", "../demo.Server")
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

var webfrontend = builder.AddViteApp("webfrontend", "../frontend")
    .WithReference(server)
    .WaitFor(server);

server.PublishWithContainerFiles(webfrontend, "wwwroot");

builder.Build().Run();

internal static class DokployDeploymentExtensions
{
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithDokployDeploymentTarget(this IResourceBuilder<DockerComposeEnvironmentResource> environment)
    {
        if (environment.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            return environment;
        }

        var resource = environment.Resource;

        var stepAnnotations = resource.Annotations
            .OfType<PipelineStepAnnotation>()
            .ToArray();

        foreach (var annotation in stepAnnotations)
        {
            var wrapper = new PipelineStepAnnotation(async (factoryContext) =>
            {
                List<PipelineStep> steps = [.. await annotation.CreateStepsAsync(factoryContext).ConfigureAwait(false)];

                steps.RemoveAll(s => s.Name == $"docker-compose-up-{resource.Name}");

                steps.Add(new PipelineStep
                {
                    Name = $"docker-compose-up-{resource.Name}",
                    Description = $"Deploy resources for environment {resource.Name} using Dokploy",
                    Tags = ["docker-compose-up"],
                    Action = ctx => Task.CompletedTask, // TODO: here we can write the logic for the Dokploy deployment
                    DependsOnSteps = [$"prepare-{resource.Name}"],
                    RequiredBySteps = [WellKnownPipelineSteps.Deploy],
                });

                return steps;
            });

            resource.Annotations.Remove(annotation);
            resource.Annotations.Add(wrapper);
        }

        return environment;
    }
}