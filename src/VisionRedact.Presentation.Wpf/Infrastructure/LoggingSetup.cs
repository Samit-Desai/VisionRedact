using System.IO;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace VisionRedact.Presentation.Wpf.Infrastructure;

// the technical log (stack traces, native error text, thread ids) goes to a rolling file, users only see friendly
// messages. built once in App.OnStartup. the code talks to ILogger, Serilog writes the file.
// %LOCALAPPDATA%\VisionRedact\logs, one file a day, the last 7 kept
public static class LoggingSetup
{
    public static string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionRedact", "logs");

    public static ILoggerFactory CreateLoggerFactory()
    {
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.With(new ThreadIdEnricher())
            .WriteTo.File(
                Path.Combine(LogDirectory, "visionredact-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] (T{ThreadId}) {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // dispose: true, so disposing the factory (App.OnExit) flushes and closes the file
        return new SerilogLoggerFactory(serilog, dispose: true);
    }

    // a thread id on every line makes work on the wrong thread visible in the log
    private sealed class ThreadIdEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) =>
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("ThreadId", Environment.CurrentManagedThreadId));
    }
}
