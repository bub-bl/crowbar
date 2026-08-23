using Crowbar.Engine.Global;

namespace Crowbar.Console.Tests;

/// <summary>
/// Tests for the <see cref="Log"/> API. Each test creates its own Log instance
/// and cleans up the EntryAdded event subscription.
/// </summary>
[Collection("Console")]
public class LogTests : IDisposable
{
    private readonly Crowbar.Engine.Global.Log _log = new();
    private Action<LogEntry>? _handler;

    public LogTests()
    {
        _log.Clear();
    }

    public void Dispose()
    {
        if (_handler is not null)
            _log.EntryAdded -= _handler;
    }

    [Fact]
    public void Info_FiresEntryAddedWithCorrectLevel()
    {
        LogEntry? captured = null;
        _handler = entry => captured = entry;
        _log.EntryAdded += _handler;

        _log.Info("test message");

        Assert.NotNull(captured);
        Assert.Equal("test message", captured!.Message);
        Assert.Equal(LogLevel.Info, captured.Level);
        Assert.Null(captured.StackTrace);
    }

    [Fact]
    public void Warn_FiresEntryAddedWithWarningLevel()
    {
        LogEntry? captured = null;
        _handler = entry => captured = entry;
        _log.EntryAdded += _handler;

        _log.Warn("warning message");

        Assert.NotNull(captured);
        Assert.Equal("warning message", captured!.Message);
        Assert.Equal(LogLevel.Warning, captured.Level);
    }

    [Fact]
    public void Error_FiresEntryAddedWithErrorLevel()
    {
        LogEntry? captured = null;
        _handler = entry => captured = entry;
        _log.EntryAdded += _handler;

        _log.Error("error message");

        Assert.NotNull(captured);
        Assert.Equal("error message", captured!.Message);
        Assert.Equal(LogLevel.Error, captured.Level);
    }

    [Fact]
    public void Error_WithException_CapturesStackTrace()
    {
        LogEntry? captured = null;
        _handler = entry => captured = entry;
        _log.EntryAdded += _handler;

        var exception = new InvalidOperationException("test exception");
        _log.Error("error with exception", exception);

        Assert.NotNull(captured);
        Assert.Equal("error with exception", captured!.Message);
        Assert.Equal(LogLevel.Error, captured.Level);
        Assert.NotNull(captured.StackTrace);
        Assert.Contains("InvalidOperationException", captured.StackTrace);
        Assert.Same(exception, captured.Exception);
    }

    [Fact]
    public void Info_WithDetails_StoresSubLines()
    {
        LogEntry? captured = null;
        _handler = entry => captured = entry;
        _log.EntryAdded += _handler;

        _log.Info("header", ["line one", "line two"]);

        Assert.NotNull(captured);
        Assert.Equal("header", captured!.Message);
        Assert.NotNull(captured.Details);
        Assert.Equal(["line one", "line two"], captured.Details);
    }

    [Fact]
    public void Entries_BuffersMessagesInOrder()
    {
        _log.Clear();
        _log.Info("first");
        _log.Warn("second");
        _log.Error("third");

        var entries = _log.Entries;
        Assert.Equal(3, entries.Count);
        Assert.Equal("first", entries[0].Message);
        Assert.Equal(LogLevel.Info, entries[0].Level);
        Assert.Equal("second", entries[1].Message);
        Assert.Equal(LogLevel.Warning, entries[1].Level);
        Assert.Equal("third", entries[2].Message);
        Assert.Equal(LogLevel.Error, entries[2].Level);
    }

    [Fact]
    public void Clear_EmptiesBufferedEntries()
    {
        _log.Info("before clear");
        Assert.NotEmpty(_log.Entries);

        _log.Clear();

        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Entry_CarriesTimestamp()
    {
        _log.Clear();
        var before = DateTime.Now;
        _log.Info("timed message");
        var after = DateTime.Now;

        var entries = _log.Entries;
        Assert.Single(entries);
        Assert.True(entries[0].Timestamp >= before && entries[0].Timestamp <= after);
    }
}