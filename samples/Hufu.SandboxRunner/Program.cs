using System.Text.Json;

namespace Hufu.SandboxRunner;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] == "help")
        {
            Console.WriteLine("Hufu.SandboxRunner (cross-platform governed execution sample). Commands:");
            Console.WriteLine("run [WORKSPACE] (execute the pinned whoami plan; prints the audit record as JSON)");
            return 0;
        }
        try
        {
            if (args[0] != "run" || args.Length > 2)
                throw new ArgumentException("Unknown command or arguments.");
            bool temporary = args.Length == 1;
            string workspace = temporary
                ? Path.Combine(Path.GetTempPath(), "sandbox-runner-" + Guid.NewGuid().ToString("N"))
                : args[1];
            SandboxRunRecord record;
            try
            {
                record = await SandboxRunner.RunAsync(workspace);
            }
            finally
            {
                if (temporary)
                    try { Directory.Delete(workspace, recursive: true); } catch { }
            }
            Console.WriteLine(JsonSerializer.Serialize(record));
            return record.Status == "Succeeded" ? 0 : 2;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Never disclose workspace paths or SQL to the tool caller.
            Console.Error.WriteLine(JsonSerializer.Serialize(new { Status = "DeniedOrUnavailable", Category = error.GetType().Name }));
            return 2;
        }
    }
}
