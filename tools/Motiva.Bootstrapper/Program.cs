using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Motiva.Application;
using Motiva.Application.Administration;
using Motiva.Infrastructure;

var services = new ServiceCollection()
    .AddMotivaApplication()
    .AddMotivaInfrastructure(options =>
    {
        options.PostgresConnectionString = Environment.GetEnvironmentVariable("Motiva__PostgresConnectionString")
            ?? "Host=localhost;Port=5433;Database=motiva;Username=motiva;Password=motiva-stand";
    })
    .BuildServiceProvider();

var actorsPath = args.Length > 0 ? args[0] : "auth/.local/actors.json";
if (!File.Exists(actorsPath))
{
    Console.Error.WriteLine("actors.json not found at " + actorsPath + ". Run: python3 auth/jwt.py prepare");
    return 1;
}

using var document = JsonDocument.Parse(await File.ReadAllTextAsync(actorsPath));
var actors = document.RootElement.EnumerateArray()
    .Select(node => new BootstrapActor(
        node.GetProperty("sub").GetString()!,
        Guid.Parse(node.GetProperty("companyId").GetString()!),
        node.GetProperty("actorType").GetString()!,
        node.TryGetProperty("masterId", out var master) && (master.ValueKind is JsonValueKind.Number or JsonValueKind.String)
            ? int.Parse(master.ToString())
            : null,
        node.TryGetProperty("role", out var role) && role.ValueKind is JsonValueKind.String or JsonValueKind.Array
            ? role.ValueKind == JsonValueKind.String
                ? role.GetString()
                : string.Join(",", role.EnumerateArray().Select(r => r.GetString()))
            : null))
    .ToList();

await services.GetRequiredService<BootstrapService>().RunAsync(actors, "Motiva", CancellationToken.None);
Console.WriteLine("Bootstrap complete: companies and active administrators ensured.");
return 0;
