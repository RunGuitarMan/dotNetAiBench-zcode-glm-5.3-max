using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Motiva.Application;
using Motiva.Infrastructure;
using Motiva.Worker;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddMotivaApplication();
builder.Services.AddMotivaInfrastructure(options =>
    builder.Configuration.GetSection(MotivaInfrastructureOptions.SectionName).Bind(options));
builder.Services.AddHostedService<OutboxDispatcherService>();
builder.Services.AddHostedService<CleanupSweepService>();
var host = builder.Build();
host.Run();
