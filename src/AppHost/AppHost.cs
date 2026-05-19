var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.DeveloperAgent>("web");

builder.Build().Run();
