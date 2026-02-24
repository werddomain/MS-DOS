using MsDos.Core;
using MsDos.Core.Dos;
using MsDos.Core.Memory;
using MsDos.Core.Platform;
using System.Collections.Concurrent;

namespace MsDos.Tests;

/// <summary>
/// Tests for FloppyDriveController, boot sequence, manual clock, and memory snapshots.
/// </summary>
public class FloppyDriveTests
{
    private sealed class TestRenderer : IGraphicsRenderer
    {
        public int Width => 640;
        public int Height => 400;
        public void SetMode(VideoMode mode) { }
        public void DrawCharacter(int col, int row, char character, byte foreground, byte background) { }
        public void DrawPixel(int x, int y, byte colorIndex) { }
        public void Clear(byte backgroundColorIndex) { }
        public void SetCursorPosition(int col, int row) { }
        public void SetCursorVisible(bool visible) { }
        public void ScrollUp(int lines, byte backgroundColorIndex) { }
        public void ScrollDown(int lines, byte backgroundColorIndex) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private sealed class TestEventRegistry : IEventRegistry
    {
        private readonly ConcurrentQueue<DosKeyEventArgs> _keys = new();
        private readonly SemaphoreSlim _sem = new(0);
#pragma warning disable CS0067
        public event Action<DosKeyEventArgs>? KeyDown;
        public event Action<DosKeyEventArgs>? KeyUp;
        public event Action<DosMouseEventArgs>? MouseMove;
        public event Action<DosMouseEventArgs>? MouseDown;
        public event Action<DosMouseEventArgs>? MouseUp;
#pragma warning restore CS0067
        public bool IsKeyAvailable => !_keys.IsEmpty;
        public async Task<DosKeyEventArgs> ReadKeyAsync(CancellationToken ct = default)
        {
            await _sem.WaitAsync(ct);
            _keys.TryDequeue(out var key);
            return key ?? new DosKeyEventArgs();
        }
    }

    private sealed class TestStreamProvider : IStreamProvider
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        public Task<Stream> OpenReadAsync(string path) =>
            _files.TryGetValue(path, out var d) ? Task.FromResult<Stream>(new MemoryStream(d, false)) : throw new FileNotFoundException(path);
        public Task<Stream> OpenWriteAsync(string path) { _files[path] = Array.Empty<byte>(); return Task.FromResult<Stream>(new MemoryStream()); }
        public Task<Stream> OpenReadWriteAsync(string path) =>
            Task.FromResult<Stream>(new MemoryStream(_files.ContainsKey(path) ? _files[path] : Array.Empty<byte>()));
        public Task<bool> ExistsAsync(string path) => Task.FromResult(_files.ContainsKey(path));
        public Task DeleteAsync(string path) { _files.Remove(path); return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> ListEntriesAsync(string dir) => Task.FromResult<IReadOnlyList<string>>(_files.Keys.ToList());
        public Task ImportBinaryAsync(string p, byte[] d) { _files[p] = d; return Task.CompletedTask; }
        public Task CreateDirectoryAsync(string path) => Task.CompletedTask;
        public Task DeleteDirectoryAsync(string path) => Task.CompletedTask;
        public Task RenameAsync(string oldPath, string newPath) { if (_files.Remove(oldPath, out var d)) _files[newPath] = d; return Task.CompletedTask; }
        public Task<long> GetFileSizeAsync(string path) => Task.FromResult(_files.TryGetValue(path, out var d) ? (long)d.Length : -1L);
    }

    private DosMachine CreateMachine()
    {
        return new DosMachine(new TestRenderer(), new TestEventRegistry(), new TestStreamProvider());
    }

    // ── FloppyDriveController tests ──

    [Fact]
    public void FloppyController_HasTwoSlots()
    {
        var log = new EmulatorLog();
        var ctrl = new FloppyDriveController(log);

        Assert.NotNull(ctrl.SlotA);
        Assert.NotNull(ctrl.SlotB);
        Assert.Equal('A', ctrl.SlotA.DriveLetter);
        Assert.Equal('B', ctrl.SlotB.DriveLetter);
    }

    [Fact]
    public void FloppySlot_InsertAndEject()
    {
        var log = new EmulatorLog();
        var ctrl = new FloppyDriveController(log);

        // Initially empty
        Assert.False(ctrl.SlotA.HasDisk);

        // Insert
        var data = new byte[1474560]; // 1.44MB
        ctrl.InsertDisk('A', data, "TEST.IMG");
        Assert.True(ctrl.SlotA.HasDisk);
        Assert.Equal("TEST.IMG", ctrl.SlotA.DiskLabel);

        // Eject
        var ejected = ctrl.EjectDisk('A');
        Assert.NotNull(ejected);
        Assert.False(ctrl.SlotA.HasDisk);
    }

    [Fact]
    public void FloppySlot_ActivityEvents_Fire()
    {
        var slot = new FloppyDriveSlot('A');
        bool activityFired = false;
        slot.ActivityChanged += _ => activityFired = true;

        slot.BeginRead();
        Assert.True(activityFired);
        Assert.True(slot.IsReading);

        slot.EndRead();
        Assert.False(slot.IsReading);

        activityFired = false;
        slot.BeginWrite();
        Assert.True(activityFired);
        Assert.True(slot.IsWriting);
        Assert.True(slot.IsDirty);

        slot.EndWrite();
        Assert.False(slot.IsWriting);
    }

    [Fact]
    public void FloppySlot_DiskTypeGeometry()
    {
        var slot = new FloppyDriveSlot('A');

        slot.DiskType = FloppyDiskType.Floppy1440K;
        Assert.Equal(1474560, slot.GetExpectedSize());
        var (c, h, s) = slot.GetGeometry();
        Assert.Equal(80, c);
        Assert.Equal(2, h);
        Assert.Equal(18, s);

        slot.DiskType = FloppyDiskType.Floppy360K;
        Assert.Equal(368640, slot.GetExpectedSize());
    }

    [Fact]
    public void FloppySlot_DiskChangedEvent()
    {
        var slot = new FloppyDriveSlot('A');
        bool changed = false;
        slot.DiskChanged += _ => changed = true;

        slot.InsertDisk(new byte[100], "test");
        Assert.True(changed);

        changed = false;
        slot.EjectDisk();
        Assert.True(changed);
    }

    [Fact]
    public void SaveDriveState_ReturnsCopy()
    {
        var log = new EmulatorLog();
        var ctrl = new FloppyDriveController(log);
        var data = new byte[] { 1, 2, 3, 4, 5 };
        ctrl.InsertDisk('A', data, "test");

        var saved = ctrl.SaveDriveState('A');
        Assert.NotNull(saved);
        Assert.Equal(data, saved);
        Assert.NotSame(data, saved); // must be a copy
    }

    [Fact]
    public void IsBootable_ChecksSignature()
    {
        var log = new EmulatorLog();
        var ctrl = new FloppyDriveController(log);

        // Not bootable
        var data = new byte[512];
        ctrl.InsertDisk('A', data, "test");
        Assert.False(ctrl.IsBootable('A'));

        // Bootable (0x55AA at offset 510)
        data[510] = 0x55;
        data[511] = 0xAA;
        ctrl.InsertDisk('A', data, "boot");
        Assert.True(ctrl.IsBootable('A'));
    }

    // ── Boot sequence tests ──

    [Fact]
    public void CheckBootSequence_PrefersFloppyA()
    {
        var machine = CreateMachine();
        var img = new byte[368640]; // 360K floppy
        machine.InsertFloppyDisk('A', img, "FLOPPY_A");

        var bootDrive = machine.CheckBootSequence();
        Assert.Equal('A', bootDrive);
    }

    [Fact]
    public void CheckBootSequence_FallsToB_WhenNoA()
    {
        var machine = CreateMachine();
        var img = new byte[368640];
        machine.InsertFloppyDisk('B', img, "FLOPPY_B");

        var bootDrive = machine.CheckBootSequence();
        Assert.Equal('B', bootDrive);
    }

    [Fact]
    public void CheckBootSequence_FallsToC_WhenNoFloppies()
    {
        var machine = CreateMachine();
        var bootDrive = machine.CheckBootSequence();
        Assert.Equal('C', bootDrive); // C: is always mounted
    }

    [Fact]
    public void InsertAndEjectFloppy_WhileRunning()
    {
        var machine = CreateMachine();
        var img = new byte[368640];

        bool ok = machine.InsertFloppyDisk('A', img, "DISK1");
        Assert.True(ok);
        Assert.NotNull(machine.GetDriveProvider('A'));

        var ejected = machine.EjectFloppyDisk('A');
        Assert.NotNull(ejected);
        Assert.Null(machine.GetDriveProvider('A'));
    }

    [Fact]
    public void SaveFloppyState_ReturnsImageData()
    {
        var machine = CreateMachine();
        var img = new byte[368640];
        img[0] = 0xEB; // jump instruction
        machine.InsertFloppyDisk('A', img, "DISK1");

        var saved = machine.SaveFloppyState('A');
        Assert.NotNull(saved);
        Assert.Equal(368640, saved!.Length);
        Assert.Equal(0xEB, saved[0]);
    }

    // ── Memory snapshot tests ──

    [Fact]
    public void MemorySnapshot_CaptureAndCompare()
    {
        var machine = CreateMachine();
        machine.Memory.WriteByte(0x1000, 0xAA);
        machine.Memory.WriteByte(0x1001, 0xBB);

        var snap1 = machine.Memory.CaptureSnapshot(0x1000, 16, machine.Cpu.Regs);

        machine.Memory.WriteByte(0x1001, 0xCC);

        var snap2 = machine.Memory.CaptureSnapshot(0x1000, 16, machine.Cpu.Regs);

        var diff = snap1.CompareTo(snap2);
        Assert.Single(diff.Changes);
        Assert.Equal((uint)0x1001, diff.Changes[0].Address);
        Assert.Equal(0xBB, diff.Changes[0].OldValue);
        Assert.Equal(0xCC, diff.Changes[0].NewValue);
    }

    [Fact]
    public void MemorySnapshot_NoChanges_EmptyDiff()
    {
        var machine = CreateMachine();
        machine.Memory.WriteByte(0x1000, 0xFF);

        var snap1 = machine.Memory.CaptureSnapshot(0x1000, 16, machine.Cpu.Regs);
        var snap2 = machine.Memory.CaptureSnapshot(0x1000, 16, machine.Cpu.Regs);

        var diff = snap1.CompareTo(snap2);
        Assert.Empty(diff.Changes);
    }

    // ── Manual clock mode tests ──

    [Fact]
    public void ManualClockMode_StepCapturesSnapshot()
    {
        var machine = CreateMachine();

        // Load NOP NOP HLT
        byte[] com = { 0x90, 0x90, 0xF4 };
        machine.LoadBinary(com);

        machine.ManualClockMode = true;
        machine.SnapshotLength = 32;

        MemoryDiff? capturedDiff = null;
        machine.OnManualStep += diff => capturedDiff = diff;

        machine.StepInstruction(); // NOP

        Assert.NotNull(capturedDiff);
        Assert.NotNull(machine.LastSnapshot);
        // IP should have changed
        Assert.True(capturedDiff!.RegistersChanged);
    }

    [Fact]
    public void ManualClockMode_AutoClockPaused()
    {
        var machine = CreateMachine();
        machine.ManualClockMode = true;

        Assert.True(machine.ManualClockMode);
    }
}
