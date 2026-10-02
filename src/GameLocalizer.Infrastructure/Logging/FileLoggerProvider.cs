using Microsoft.Extensions.Logging;
namespace GameLocalizer.Infrastructure.Logging;

public sealed class FileLoggerProvider(string directory) : ILoggerProvider
{
    private readonly object gate = new();
    private readonly string logDirectory = directory;
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }
    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            try
            {
                lock (owner.gate)
                {
                    Directory.CreateDirectory(owner.logDirectory);
                    File.AppendAllText(Path.Combine(owner.logDirectory, DateTime.Today.ToString("yyyy-MM-dd") + ".log"),
                        $"{DateTimeOffset.Now:O} [{logLevel}] {category}: {formatter(state, null)}{(exception == null ? "" : Environment.NewLine + exception.ToString())}{Environment.NewLine}");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine(e.GetType().Name); }
        }
    }
}
