using System.Globalization;
using System.Text.Json;

namespace Hufu.LocalHost;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] == "help")
        {
            Console.WriteLine("Hufu.LocalHost (Windows NTFS, explicit local operator). Commands:");
            Console.WriteLine("init ROOT TARGET (initial UTF-8 content on stdin)");
            Console.WriteLine("activate ROOT (resume/finalize explicit bootstrap)");
            Console.WriteLine("preview ROOT OPERATION OFFSET DELETE_LENGTH (replacement UTF-8 content on stdin)");
            Console.WriteLine("review ROOT OPERATION (complete escaped before/after review, at most 8 KiB each)");
            Console.WriteLine("approve ROOT OPERATION EXACT_ADMISSION_ID [LIFETIME_SECONDS]");
            Console.WriteLine("apply|inspect|withdraw ROOT OPERATION");
            Console.WriteLine("revoke ROOT");
            return 0;
        }
        try
        {
            LocalHostResult result;
            if (args[0] == "init" && args.Length == 3)
                result = await LocalHostApplication.InitializeAsync(args[1], args[2], await InputBase64Async());
            else if (args[0] is "revoke" or "activate" && args.Length == 2)
            {
                using var host = LocalHostApplication.Open(args[1], LocalHostMode.Operator);
                result = args[0] == "activate" ? await host.ActivateAsync() : await host.RevokeAsync();
            }
            else if (args.Length >= 3)
            {
                var worker = args[0] is "preview" or "apply";
                using var host = LocalHostApplication.Open(args[1], worker ? LocalHostMode.Worker : LocalHostMode.Operator, args[2]);
                if (args[0] == "review" && args.Length == 3)
                {
                    var review = await host.ReviewAsync(args[2]);
                    Console.WriteLine(JsonSerializer.Serialize(review));
                    return review.Facts.Status == "Reviewed" ? 0 : 2;
                }
                result = args[0] switch
                {
                    "preview" when args.Length == 5 => await host.PreviewAsync(args[2], Number(args[3]), Number(args[4]), await InputBase64Async()),
                    "approve" when args.Length is 4 or 5 => await host.ApproveAsync(args[2], args[3], args.Length == 5 ? Number(args[4]) : 300),
                    "apply" when args.Length == 3 => await host.ApplyAsync(args[2]),
                    "inspect" when args.Length == 3 => await host.InspectAsync(args[2]),
                    "withdraw" when args.Length == 3 => await host.WithdrawAsync(args[2]),
                    _ => throw new ArgumentException("Unknown command or arguments.")
                };
            }
            else throw new ArgumentException("Unknown command or arguments.");
            Console.WriteLine(JsonSerializer.Serialize(result));
            return result.Status is "Initialized" or "Prepared" or "Recorded" or "Replayed" or "Succeeded" or "Completed" or "Applied" or "Revoked" ? 0 : 2;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Never disclose exceptions containing control paths, SQL, identity
            // facts or replacement bytes to the tool caller.
            Console.Error.WriteLine(JsonSerializer.Serialize(new { Status = "DeniedOrUnavailable", Category = error.GetType().Name }));
            return 2;
        }
    }
    private static int Number(string value) => int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    private static async Task<string> InputBase64Async()
    {
        using var input = Console.OpenStandardInput();
        using var captured = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer)) > 0)
        {
            if (captured.Length + count > 1_048_576) throw new ArgumentException("UTF-8 input exceeds 1 MiB.");
            captured.Write(buffer, 0, count);
        }
        return Convert.ToBase64String(captured.ToArray());
    }
}
