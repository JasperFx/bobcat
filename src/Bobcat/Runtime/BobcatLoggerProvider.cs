using Bobcat.Engine;
using Microsoft.Extensions.Logging;

namespace Bobcat.Runtime;

/// <summary>
/// ILoggerProvider that captures log messages and correlates them to the
/// currently executing step. Register this in the IHost's service collection
/// to get correlated logs in test results.
/// </summary>
public class BobcatLoggerProvider : ILoggerProvider
{
    private SpecExecutionContext? _context;
    private LogLevel _minimumLevel;

    public BobcatLoggerProvider(LogLevel minimumLevel = LogLevel.Information)
    {
        _minimumLevel = minimumLevel;
    }

    /// <summary>
    /// Set the current execution context. Called by BobcatRunner before each scenario.
    /// </summary>
    public void SetContext(SpecExecutionContext? context)
    {
        _context = context;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new BobcatLogger(this, categoryName);
    }

    public void Dispose() { }

    private class BobcatLogger : ILogger
    {
        private readonly BobcatLoggerProvider _provider;
        private readonly string _category;

        public BobcatLogger(BobcatLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _provider._minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var step = _provider._context?.CurrentStep;
            if (step == null) return;

            var message = formatter(state, exception);
            var prefix = logLevel switch
            {
                LogLevel.Trace => "TRACE",
                LogLevel.Debug => "DEBUG",
                LogLevel.Information => "INFO",
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                LogLevel.Critical => "CRIT",
                _ => logLevel.ToString()
            };

            var logLine = $"[{prefix}] {_category}: {message}";
            if (exception != null)
            {
                logLine += $"\n  {exception.GetType().Name}: {exception.Message}";
            }

            step.AddLog(logLine);
        }
    }
}
