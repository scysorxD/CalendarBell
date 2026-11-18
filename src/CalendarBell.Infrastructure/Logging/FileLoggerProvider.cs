using System.Globalization;
using System.Text;
using CalendarBell.Infrastructure.Security;
using Microsoft.Extensions.Logging;

namespace CalendarBell.Infrastructure.Logging;

/// <summary>
/// Local rolling log (spec §9.1): calendarbell.log + up to 4 rotated files of ≤ 2 MiB, deleted after 14 days.
/// Every line, including exception text, passes through <see cref="LogSanitizer"/> (no URLs or query strings).
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const string BaseName = "calendarbell";

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly long _maxFileBytes;
    private readonly int _maxFiles;
    private readonly TimeSpan _retention;
    private readonly LogLevel _minLevel;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private bool _disposed;

    public FileLoggerProvider(string directory, TimeProvider time, long maxFileBytes = 2 * 1024 * 1024, int maxFiles = 5, TimeSpan? retention = null, LogLevel minLevel = LogLevel.Information)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _maxFileBytes = maxFileBytes;
        _maxFiles = Math.Max(1, maxFiles);
        _retention = retention ?? TimeSpan.FromDays(14);
        _minLevel = minLevel;
        Directory.CreateDirectory(_directory);
        DeleteExpired();
    }

    public string CurrentFilePath => Path.Combine(_directory, BaseName + ".log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, ShortCategory(categoryName));

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        if (level < _minLevel || level == LogLevel.None)
        {
            return;
        }

        var line = new StringBuilder(160)
            .Append(_time.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
            .Append(' ').Append(LevelCode(level))
            .Append(" [").Append(category).Append("] ")
            .Append(LogSanitizer.Redact(message));
        if (exception is not null)
        {
            line.Append(Environment.NewLine).Append(LogSanitizer.Redact(exception.ToString()));
        }

        var text = line.Append(Environment.NewLine).ToString();
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var writer = EnsureWriter();
                if (writer.BaseStream.Length + Encoding.UTF8.GetByteCount(text) > _maxFileBytes && writer.BaseStream.Length > 0)
                {
                    Rotate();
                    writer = EnsureWriter();
                }

                writer.Write(text);
                writer.Flush();
            }
            catch (IOException)
            {
                // Logging must never take the app down (e.g. disk full).
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private StreamWriter EnsureWriter()
    {
        if (_writer is null)
        {
            var stream = new FileStream(CurrentFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(stream, new UTF8Encoding(false));
        }

        return _writer;
    }

    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;
        var oldest = Path.Combine(_directory, $"{BaseName}.{_maxFiles - 1}.log");
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var i = _maxFiles - 2; i >= 1; i--)
        {
            var from = Path.Combine(_directory, $"{BaseName}.{i}.log");
            if (File.Exists(from))
            {
                File.Move(from, Path.Combine(_directory, $"{BaseName}.{i + 1}.log"), overwrite: true);
            }
        }

        if (_maxFiles > 1 && File.Exists(CurrentFilePath))
        {
            File.Move(CurrentFilePath, Path.Combine(_directory, $"{BaseName}.1.log"), overwrite: true);
        }
        else if (File.Exists(CurrentFilePath))
        {
            File.Delete(CurrentFilePath);
        }

        DeleteExpired();
    }

    private void DeleteExpired()
    {
        var cutoff = _time.GetUtcNow().UtcDateTime - _retention;
        foreach (var file in Directory.EnumerateFiles(_directory, BaseName + "*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private static string LevelCode(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= owner._minLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (IsEnabled(logLevel))
            {
                owner.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
