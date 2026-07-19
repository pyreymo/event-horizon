using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
#if DEBUG
using Serilog;
using Serilog.Core;
using Serilog.Events;
#endif

namespace EventHorizon.Integration.Debug;

internal static class DebugFileLog
{
#if DEBUG
    private const long FileSizeLimitBytes = 16 * 1024 * 1024;
    private static readonly Lock StateLock = new();
    private static readonly ConcurrentDictionary<string, ILogger> SourceLoggers = new();
    private static ILogger? Logger;
    private static IDalamudPluginInterface? PluginInterface;
    private static IPluginLog? PluginLog;
#endif

    [Conditional("DEBUG")]
    public static void Initialize(IDalamudPluginInterface pluginInterface, IPluginLog pluginLog)
    {
#if DEBUG
        lock (StateLock)
        {
            PluginInterface = pluginInterface;
            PluginLog = pluginLog;
            if (Logger != null)
            {
                return;
            }

            CreateLogger(pluginInterface, pluginLog);
        }
#endif
    }

    [Conditional("DEBUG")]
    public static void Debug(string source, string messageTemplate, params object?[] propertyValues)
    {
#if DEBUG
        GetSourceLogger(source)?.Debug(messageTemplate, propertyValues);
#endif
    }

    [Conditional("DEBUG")]
    public static void Information(string source, string messageTemplate, params object?[] propertyValues)
    {
#if DEBUG
        GetSourceLogger(source)?.Information(messageTemplate, propertyValues);
#endif
    }

    [Conditional("DEBUG")]
    public static void Warning(string source, string messageTemplate, params object?[] propertyValues)
    {
#if DEBUG
        GetSourceLogger(source)?.Warning(messageTemplate, propertyValues);
#endif
    }

    [Conditional("DEBUG")]
    public static void Error(string source, Exception exception, string messageTemplate, params object?[] propertyValues)
    {
#if DEBUG
        GetSourceLogger(source)?.Error(exception, messageTemplate, propertyValues);
#endif
    }

    [Conditional("DEBUG")]
    public static void Close()
    {
#if DEBUG
        lock (StateLock)
        {
            if (Logger == null)
            {
                return;
            }

            GetSourceLogger("DebugFileLog")?.Information("Debug file logging stopped");
            DisposeLogger();
            PluginInterface = null;
            PluginLog = null;
        }
#endif
    }

#if DEBUG
    private static void CreateLogger(IDalamudPluginInterface pluginInterface, IPluginLog pluginLog)
    {
        try
        {
            var directory = Path.Combine(pluginInterface.ConfigDirectory.FullName, "debug-logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "event-horizon-.log");
            Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.With<CurrentThreadIdEnricher>()
                .WriteTo.File(
                    path,
                    restrictedToMinimumLevel: LogEventLevel.Debug,
                    outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] [T{ThreadId}] [{Source}] {Message:lj}{NewLine}{Exception}",
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: FileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: 7,
                    retainedFileTimeLimit: TimeSpan.FromDays(7),
                    buffered: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1)
                )
                .CreateLogger();

            GetSourceLogger("DebugFileLog")?.Information("Debug file logging started at {Path}", path);
            pluginLog.Information("Debug file log: {Path}", path);
        }
        catch (Exception exception)
        {
            DisposeLogger();
            pluginLog.Warning(exception, "Failed to initialize the debug file log.");
        }
    }

    private static void DisposeLogger()
    {
        (Logger as IDisposable)?.Dispose();
        Logger = null;
        SourceLoggers.Clear();
    }

    private static ILogger? GetSourceLogger(string source)
    {
        var current = Logger;
        return current == null ? null : SourceLoggers.GetOrAdd(source, static (name, root) => root.ForContext("Source", name), current);
    }

    private sealed class CurrentThreadIdEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) =>
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("ThreadId", Environment.CurrentManagedThreadId));
    }
#endif
}
