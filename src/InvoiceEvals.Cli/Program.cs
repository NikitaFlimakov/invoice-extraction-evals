using System.CommandLine;
using InvoiceEvals.Cli;

var root = new RootCommand("Invoice extraction benchmark and evaluation harness.")
{
    DownloadCommand.Create(),
    SynthesizeCommand.Create(),
    RunCommand.Create(),
    ReportCommand.Create(),
    CompareCommand.Create(),
};
return await root.Parse(args).InvokeAsync();
