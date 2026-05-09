#pragma warning disable ASPIRECSHARPAPPS001

using Scalar.Aspire;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("demo")
    .WithDokployDeploymentTarget();

var database = builder.AddDokployPostgres("database");

builder.AddPostgres("p2")
    .PublishAsDokployDatabase();

builder.AddDokployRedis("redis");
builder.AddRedis("r2")
    .PublishAsDokployDatabase();

builder.AddDokployMySql("mysql");
builder.AddMySql("m2")
    .PublishAsDokployDatabase();

builder.AddDokployMariaDB("maria");
builder.AddMySql("ma2")
    .PublishAsDokployMariaDB();

builder.AddDokployMongoDB("mongodb");
builder.AddMongoDB("mongo2")
    .PublishAsDokployDatabase();

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
