#pragma warning disable ASPIRECSHARPAPPS001

using Scalar.Aspire;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("demo")
    .WithDokployDeploymentTarget();

var database = builder.AddDokployPostgres("database")
    .AddDatabase("mydb");

var server = builder.AddCSharpApp("server", "../demo.Server")
    .WithReference(database)
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
