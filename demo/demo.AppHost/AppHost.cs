#pragma warning disable ASPIRECSHARPAPPS001

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDokployEnvironment("demo");

builder.AddDockerComposeEnvironment("docker-compose-demo")
    .WithContainerRegistry("mcr.microsoft.com");

var server = builder.AddCSharpApp("server", "../demo.Server")
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

var mariadb = builder.AddDokployMariaDB("mariadb");
var mongodb = builder.AddDokployMongoDB("mongodb");
var mysql = builder.AddDokployMySql("mysql");
var postgres = builder.AddDokployPostgres("postgres");
var redis = builder.AddDokployRedis("redis");

var webfrontend = builder.AddViteApp("webfrontend", "../frontend")
    .WithReference(server)
    .WaitFor(server);

server.PublishWithContainerFiles(webfrontend, "wwwroot");

// Test the database integrations by referencing the database resources from the server resource, to ensure that the resource definitions are correct and can be used by other resources in the application.

server.WithReference(mariadb)
    .WithReference(mongodb)
    .WithReference(mysql)
    .WithReference(postgres)
    .WithReference(redis);

// Test the database integrations with custom container images as well, to ensure that the resource definitions are correct and can be used by other resources in the application.

var containerMariadb = builder.AddMySql("container-mariadb");
var containerMongodb = builder.AddMongoDB("container-mongodb");
var containerMysql = builder.AddMySql("container-mysql");
var containerPostgres = builder.AddPostgres("container-postgres");
var containerRedis = builder.AddRedis("container-redis");

server.WithReference(containerMariadb)
    .WithReference(containerMongodb)
    .WithReference(containerMysql)
    .WithReference(containerPostgres)
    .WithReference(containerRedis);

builder.Build().Run();
