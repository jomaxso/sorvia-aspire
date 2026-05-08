#pragma warning disable ASPIRECSHARPAPPS001
#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.Docker;
using Scalar.Aspire;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("demo")
    .WithDokployDeploymentTarget();

var server = builder.AddCSharpApp("server", "../demo.Server")
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

var docs = builder.AddScalarApiReference("scalar", options =>
        options.WithTheme(ScalarTheme.Solarized))
    .WithApiReference(server)
    .WithExternalHttpEndpoints();

var webfrontend = builder.AddViteApp("webfrontend", "../frontend")
    .WithReference(server)
    .WithReference(docs)
    .WaitFor(server);

server.PublishWithContainerFiles(webfrontend, "wwwroot");

builder.Build().Run();
