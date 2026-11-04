using SemanticBridge.Cli;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

return await CliApp.RunAsync(args, Console.Out, Console.Error, Console.In, Environment.GetEnvironmentVariable, () => Console.IsInputRedirected, cts.Token);
