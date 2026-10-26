using Microsoft.Extensions.Logging;
using ReVue.Diagnostics;

namespace ReVueVRO.Services;

// Capture existing CSS/server warnings without enabling verbose HTTP request logging.
internal sealed class RuntimeLogProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName);
    public void Dispose() { } // The application entry point owns RuntimeLog's lifetime.

    private sealed class FileLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level != LogLevel.None &&
            (level >= LogLevel.Warning || (category.StartsWith("ReVue", StringComparison.Ordinal) && level >= LogLevel.Information));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            try { RuntimeLog.Write(logLevel.ToString(), category, formatter(state, exception), exception); }
            catch { }
        }
    }
}
