using Microsoft.Extensions.Logging;

namespace MesasLog.Infrastructure.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly LogLevel _minLevel;
    private readonly object _lock = new();

    public FileLoggerProvider(string directory, LogLevel minLevel)
    {
        _directory = directory;
        _minLevel = minLevel;
        Directory.CreateDirectory(_directory);
    }

    public ILogger CreateLogger(string categoryName) => new FileCategoryLogger(categoryName, _directory, _minLevel, _lock);

    public void Dispose() { }

    private sealed class FileCategoryLogger : ILogger
    {
        private readonly string _category;
        private readonly string _directory;
        private readonly LogLevel _min;
        private readonly object _lock;

        public FileCategoryLogger(string category, string directory, LogLevel min, object lockObj)
        {
            _category = category;
            _directory = directory;
            _min = min;
            _lock = lockObj;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _min && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {_category}: {formatter(state, exception)}";
            if (exception != null) line += Environment.NewLine + exception;
            var file = Path.Combine(_directory, $"{DateTime.Now:yyyy-MM-dd}.log");
            lock (_lock)
            {
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
    }
}
