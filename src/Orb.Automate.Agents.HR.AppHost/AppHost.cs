var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Orb_Automate_Agents_HR_Api>("hr-api")
    .WithHttpHealthCheck("/health/ready");

builder.AddDevUI("devui")
    .WithAgentService(api, agents: [new("ARIA", "HR assistant agent backed by Azure AI Foundry.")])
    .WaitFor(api);

builder.Build().Run();
