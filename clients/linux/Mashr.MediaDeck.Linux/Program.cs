using System.Text.Json;
using Mashr.MediaDeck.Linux;

return await Cli.RunAsync(args);

static class Cli
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            Console.WriteLine("MASHR Media Deck Linux provider scaffold");
            Console.WriteLine("  probe");
            Console.WriteLine("  command play|pause|stop|next|previous|back10|forward10");
            Console.WriteLine();
            Console.WriteLine("No phone listener is enabled in this scaffold.");
            return 0;
        }

        var provider = new PlayerctlMediaProvider();
        if (args is ["probe"])
        {
            var result = await provider.ProbeAsync();
            if (!result.Success)
            {
                Console.Error.WriteLine(result.Error);
                return result.ExitCode;
            }
            Console.WriteLine(JsonSerializer.Serialize(result.Snapshot, Json));
            return 0;
        }

        if (args is ["command", var command])
        {
            var result = await provider.ControlAsync(command);
            if (!result.Success)
            {
                Console.Error.WriteLine(result.Error);
                return result.ExitCode;
            }
            Console.WriteLine(JsonSerializer.Serialize(new { action = command, provider = "playerctl" }, Json));
            return 0;
        }

        Console.Error.WriteLine("Unknown arguments. Run with --help.");
        return 2;
    }
}

