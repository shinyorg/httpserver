using System.CommandLine;
using Shiny.Net.HttpServer.CommandLine;

return await Cli
    .Build(Runner.RunAsync)
    .Parse(args)
    .InvokeAsync()
    .ConfigureAwait(false);
