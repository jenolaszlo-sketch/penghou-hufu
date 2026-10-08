using System.Text.Json;
using Hufu.PlanHost;

if (args.Length == 0 || args[0] == "help")
{
    Console.WriteLine("Hufu.PlanHost (trusted host for admitted Fuwen plans). Commands:");
    Console.WriteLine("catalogue (print the trusted catalogue and policy identity)");
    Console.WriteLine("start [WORKSPACE] [--input JSON] (admit the demo plan and start a durable run; prints the run id)");
    Console.WriteLine("After start, inspect the run with: zhinu --db <workspace>/plan-host.db runs show <run-id>");
    return 0;
}

try
{
    if (args[0] == "catalogue" && args.Length == 1)
    {
        Console.WriteLine(JsonSerializer.Serialize(AdmittedPlanHost.Describe()));
        return 0;
    }

    if (args[0] == "start")
    {
        string workspace = Path.Combine(Path.GetTempPath(), "plan-host-" + Guid.NewGuid().ToString("N"));
        string inputJson = "\"go\"";
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--input" && i + 1 < args.Length) inputJson = args[++i];
            else if (args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unknown option '{args[i]}'.");
            else workspace = args[i];
        }

        var record = await AdmittedPlanHost.StartAsync(workspace, inputJson);
        Console.WriteLine(JsonSerializer.Serialize(record));
        return record.Status == "Started" ? 0 : 2;
    }

    throw new ArgumentException("Unknown command or arguments.");
}
catch (Exception error) when (error is not OutOfMemoryException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { Status = "DeniedOrUnavailable", Category = error.GetType().Name }));
    return 2;
}
