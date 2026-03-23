using System.Collections.Concurrent;

namespace HomelabBackup.Web.Services;

public sealed class InMemoryLoggerProvider : ILoggerProvider
{
    private readonly BackupStateService _stateService;
    private readonly ConcurrentDictionary<string, InMemoryLogger> _loggers = new();
    private readonly CircularBuffer<LogEntry> _buffer = new(1000);

    public IReadOnlyList<LogEntry> Entries => _buffer.ToList();

    public InMemoryLoggerProvider(BackupStateService stateService)
    {
        _stateService = stateService;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new InMemoryLogger(name, this));
    }

    internal void AddEntry(LogEntry entry)
    {
        _buffer.Add(entry);
        _stateService.ReportLog(entry);
    }

    public void Dispose() { }
}

internal sealed class InMemoryLogger : ILogger
{
    private readonly string _categoryName;
    private readonly InMemoryLoggerProvider _provider;

    public InMemoryLogger(string categoryName, InMemoryLoggerProvider provider)
    {
        _categoryName = categoryName;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var source = _categoryName.Contains('.')
            ? _categoryName[((_categoryName.LastIndexOf('.') + 1))..]
            : _categoryName;

        var message = formatter(state, exception);
        if (exception is not null)
            message += $"\n{exception}";

        _provider.AddEntry(new LogEntry(DateTime.UtcNow, logLevel.ToString(), source, message));
    }
}

internal sealed class CircularBuffer<T>
{
    private readonly T[] _buffer;
    private int _head;
    private int _count;
    private readonly object _lock = new();

    public CircularBuffer(int capacity)
    {
        _buffer = new T[capacity];
    }

    public void Add(T item)
    {
        lock (_lock)
        {
            _buffer[_head] = item;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length)
                _count++;
        }
    }

    public List<T> ToList()
    {
        lock (_lock)
        {
            var result = new List<T>(_count);
            var start = _count < _buffer.Length ? 0 : _head;
            for (int i = 0; i < _count; i++)
            {
                result.Add(_buffer[(start + i) % _buffer.Length]);
            }
            return result;
        }
    }
}
