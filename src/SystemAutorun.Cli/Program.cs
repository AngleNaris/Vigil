using System.Text;
using SystemAutorun.Cli;

Console.InputEncoding = new UTF8Encoding(false, true);
Console.OutputEncoding = new UTF8Encoding(false);
using var stopped = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; stopped.Cancel(); };
Console.CancelKeyPress += cancel;
try { return await new CliApplication().RunAsync(args, Console.In, Console.Out, Console.Error, stopped.Token); }
finally { Console.CancelKeyPress -= cancel; }
