using MsDos.Core.Platform;

namespace MsDos.Tests;

public class EmulatorLogTests
{
    [Fact]
    public void Log_AddsEntry()
    {
        var log = new EmulatorLog();

        log.Info("Test", "Hello");

        var entries = log.GetEntries();
        Assert.Single(entries);
        Assert.Equal(LogLevel.Info, entries[0].Level);
        Assert.Equal("Test", entries[0].Source);
        Assert.Equal("Hello", entries[0].Message);
    }

    [Fact]
    public void Log_BelowMinLevel_IsDiscarded()
    {
        var log = new EmulatorLog();
        log.MinLevel = LogLevel.Warning;

        log.Info("Test", "Should be discarded");
        log.Debug("Test", "Also discarded");
        log.Warn("Test", "This one kept");

        var entries = log.GetEntries();
        Assert.Single(entries);
        Assert.Equal(LogLevel.Warning, entries[0].Level);
    }

    [Fact]
    public void Log_FiresEntryAddedEvent()
    {
        var log = new EmulatorLog();
        LogEntry? received = null;
        log.EntryAdded += entry => received = entry;

        log.Error("Src", "Msg");

        Assert.NotNull(received);
        Assert.Equal("Msg", received!.Value.Message);
    }

    [Fact]
    public void Log_RespectMaxEntries()
    {
        var log = new EmulatorLog(maxEntries: 3);

        log.Info("A", "1");
        log.Info("A", "2");
        log.Info("A", "3");
        log.Info("A", "4");

        var entries = log.GetEntries();
        Assert.Equal(3, entries.Count);
        Assert.Equal("2", entries[0].Message);
        Assert.Equal("4", entries[2].Message);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var log = new EmulatorLog();
        log.Info("A", "1");
        log.Info("A", "2");

        log.Clear();

        Assert.Empty(log.GetEntries());
    }

    [Fact]
    public void LogEntry_ToStringFormat()
    {
        var entry = new LogEntry
        {
            Timestamp = new DateTime(2025, 1, 15, 10, 30, 45, 123),
            Level = LogLevel.Error,
            Source = "CPU",
            Message = "Invalid opcode"
        };

        string s = entry.ToString();
        Assert.Contains("[Error]", s);
        Assert.Contains("CPU", s);
        Assert.Contains("Invalid opcode", s);
    }
}
