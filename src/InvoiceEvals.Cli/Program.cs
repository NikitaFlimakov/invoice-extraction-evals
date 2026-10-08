using System.CommandLine;
using InvoiceEvals.Cli;

// Tables use Δ, → and ✅/❌; the Windows console default codepage cannot show them.
Console.OutputEncoding = System.Text.Encoding.UTF8;

var root = new RootCommand("Invoice extraction benchmark and evaluation harness.")
{
    DownloadCommand.Create(),
    SynthesizeCommand.Create(),
    RunCommand.Create(),
    ReportCommand.Create(),
    CompareCommand.Create(),
    JudgeCommand.Create(),
    GateCommand.Create(),
    LangfuseCommand.Create(),
};
return await root.Parse(args).InvokeAsync();
