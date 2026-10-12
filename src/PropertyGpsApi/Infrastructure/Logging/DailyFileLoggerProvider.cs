using System.Text;
using Microsoft.Extensions.Logging;

namespace PropertyGpsApi.Infrastructure.Logging;

/// <summary>Logging:File. Read once at startup, before the container exists.</summary>
public sealed class FileLogOptions
{
    public const string Section = "Logging:File";

    public bool Enabled { get; init; } = true;

    /// <summary>Relative to the application folder, which on the server is where the install
    /// script grants the app pool write access (logs\).</summary>
    public string Path { get; init; } = "logs";

    /// <summary>app-*.log and the module's stdout_*.log older than this are deleted.</summary>
    public int RetainDays { get; init; } = 30;
}

/// <summary>
/// One file per day (app-yyyyMMdd.log), every line written as it is logged.
///
/// The IIS module's stdout log holds output back until a few thousand characters have
/// gathered or the site stops: on 2026-10-12 the test server's SMS lines only reached the
/// file when the app pool was restarted, and a quiet server shows nothing at all. Nothing
/// here is buffered, and the module's stdout files - which it never cleans up - are pruned
/// with ours.
///
/// Logging must never take the API down: a folder that cannot be written is retried at most
/// once a minute, and every failure is swallowed.
/// </summary>
[ProviderAlias("File")]
internal sealed class DailyFileLoggerProvider : ILoggerProvider
{
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly string _folder;
    private readonly int _retainDays;
    private readonly TimeProvider _clock;

    private StreamWriter? _writer;
    private DateOnly _day;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;

    public DailyFileLoggerProvider(FileLogOptions options, TimeProvider clock)
    {
        _folder = System.IO.Path.IsPathRooted(options.Path)
            ? options.Path
            : System.IO.Path.Combine(AppContext.BaseDirectory, options.Path);
        _retainDays = Math.Max(1, options.RetainDays);
        _clock = clock;
    }

    public ILogger CreateLogger(string categoryName) => new DailyFileLogger(this, categoryName);

    internal void Write(LogLevel level, string category, EventId eventId, string message)
    {
        var now = _clock.GetLocalNow();
        var line = $"{now:yyyy-MM-dd HH:mm:ss zzz} {Abbreviation(level)}: {category}[{eventId.Id}] {OneLine(message)}";

        lock (_gate)
        {
            if (now < _retryAt) return;
            try
            {
                var day = DateOnly.FromDateTime(now.DateTime);
                if (_writer is null || day != _day) Open(day, now);
                _writer!.WriteLine(line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                _writer?.Dispose();
                _writer = null;
                _retryAt = now + RetryAfterFailure;
            }
        }
    }

    private void Open(DateOnly day, DateTimeOffset now)
    {
        _writer?.Dispose();
        _writer = null;

        Directory.CreateDirectory(_folder);
        var path = System.IO.Path.Combine(_folder, $"app-{day:yyyyMMdd}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        _day = day;

        Prune(now);
    }

    /// <summary>Once a day, on the first line written. A file still held open (the module's
    /// current stdout log) cannot be deleted and is simply skipped.</summary>
    private void Prune(DateTimeOffset now)
    {
        var cutoff = now.LocalDateTime.AddDays(-_retainDays);
        foreach (var pattern in new[] { "app-*.log", "stdout_*.log" })
        {
            foreach (var file in Directory.EnumerateFiles(_folder, pattern))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>The same abbreviations the console log uses, so both read alike.</summary>
    private static string Abbreviation(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "none"
    };

    /// <summary>One entry per line, so a search for a word returns the whole entry - stack trace included.</summary>
    private static string OneLine(string text) =>
        text.Replace("\r\n", " | ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class DailyFileLogger(DailyFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Levels are filtered by the logging configuration before this is reached.
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (exception is not null) message = $"{message} {exception}";
            provider.Write(logLevel, category, eventId, message);
        }
    }
}

public static class FileLoggingExtensions
{
    /// <summary>Adds the daily log file unless Logging:File:Enabled is false.</summary>
    public static ILoggingBuilder AddDailyFile(this ILoggingBuilder logging, IConfiguration configuration)
    {
        var options = configuration.GetSection(FileLogOptions.Section).Get<FileLogOptions>() ?? new FileLogOptions();
        if (options.Enabled)
            logging.AddProvider(new DailyFileLoggerProvider(options, TimeProvider.System));
        return logging;
    }
}
