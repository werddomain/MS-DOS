using MsDos.Core;
using MsDos.Core.Dos;
using MsDos.Core.Memory;
using MsDos.Core.Platform;
using MsDos.WinForms.Platform;

namespace MsDos.WinForms;

public partial class MainForm : Form
{
    private DosMachine? _machine;
    private WinFormsRenderer? _renderer;
    private WinFormsEventRegistry? _events;
    private WinFormsStreamProvider? _streams;
    private CancellationTokenSource? _cts;
    private System.Windows.Forms.Timer? _uiWaitPumpTimer;

    // Main UI components
    private PictureBox _screen = null!;
    private MenuStrip _menu = null!;
    private StatusStrip _statusBar = null!;
    private ToolStripStatusLabel _statusLabel = null!;
    private ToolStripStatusLabel _biosWaitLabel = null!;

    // Floppy drive panels
    private Panel _drivePanel = null!;
    private Panel _driveAPanel = null!;
    private Panel _driveBPanel = null!;
    private Label _driveALabel = null!;
    private Label _driveBLabel = null!;
    private Panel _driveALed = null!;
    private Panel _driveBLed = null!;
    private ComboBox _driveAType = null!;
    private ComboBox _driveBType = null!;
    private Button _driveAInsert = null!;
    private Button _driveBInsert = null!;
    private Button _driveAEject = null!;
    private Button _driveBEject = null!;
    private Button _driveASave = null!;
    private Button _driveBSave = null!;
    private Button _driveABlank = null!;
    private Button _driveBBlank = null!;

    // Debug / Log panel
    private SplitContainer _mainSplit = null!;
    private TabControl _debugTabs = null!;
    private RichTextBox _logView = null!;
    private RichTextBox _memoryView = null!;
    private Label _registersLabel = null!;

    // Debug toolbar
    private Panel _debugToolbar = null!;
    private CheckBox _manualClockCheck = null!;
    private Button _stepButton = null!;
    private CheckBox _traceCheck = null!;
    private ComboBox _logLevelFilter = null!;
    private bool _adjustingMainSplit;

    // Activity LEDs on status bar
    private ToolStripStatusLabel _hddLed = null!;
    private ToolStripStatusLabel _cpuLed = null!;
    private ToolStripStatusLabel _powerButton = null!;

    public MainForm()
    {
        InitializeComponent();
        SetupUI();
        InitializeMachine();
    }

    private void SetupUI()
    {
        Text = "MS-DOS Emulator (.NET)";
        ClientSize = new System.Drawing.Size(1100, 700);
        BackColor = System.Drawing.Color.FromArgb(30, 30, 50);
        KeyPreview = true;
        MinimumSize = new System.Drawing.Size(900, 600);

        // ── Menu bar ──
        _menu = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("&File");
        fileMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            MakeMenuItem("&Load Binary (COM/EXE)...", Keys.Control | Keys.O, OnLoadBinary),
            MakeMenuItem("Load &Disk Image (IMG/RAW)...", Keys.Control | Keys.D, OnLoadDiskImage),
            new ToolStripSeparator(),
            MakeMenuItem("&Upload File to Drive...", Keys.Control | Keys.U, OnUploadFile),
            MakeMenuItem("Download File from Drive...", Keys.None, OnDownloadFile),
            new ToolStripSeparator(),
            MakeMenuItem("&Export C: Drive as IMG...", Keys.Control | Keys.E, OnExportCDrive),
            MakeMenuItem("&Import C: Drive from IMG...", Keys.Control | Keys.I, OnImportCDrive),
            new ToolStripSeparator(),
            MakeMenuItem("Start &Shell (COMMAND.COM)", Keys.Control | Keys.S, OnStartShell),
            MakeMenuItem("&Reset Machine", Keys.Control | Keys.R, OnReset),
            new ToolStripSeparator(),
            MakeMenuItem("E&xit", Keys.Alt | Keys.F4, (_, _) => Close()),
        });
        _menu.Items.Add(fileMenu);

        var debugMenu = new ToolStripMenuItem("&Debug");
        debugMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            MakeMenuItem("Toggle &Manual Clock", Keys.F5, (_, _) => ToggleManualClock()),
            MakeMenuItem("&Step Instruction", Keys.F10, (_, _) => DoStep()),
            MakeMenuItem("Toggle &Trace", Keys.F9, (_, _) => ToggleTrace()),
        });
        _menu.Items.Add(debugMenu);

        MainMenuStrip = _menu;
        Controls.Add(_menu);

        // ── Main split: left = screen + drives, right = debug tabs ──
        _mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            BackColor = System.Drawing.Color.FromArgb(30, 30, 50),
        };
        Controls.Add(_mainSplit);
        _mainSplit.BringToFront();
        _mainSplit.SizeChanged += (_, _) => AdjustMainSplitterDistance();

        // ── Left side: screen + floppy drive panel ──
        _screen = new PictureBox
        {
            Dock = DockStyle.Fill,
            BackColor = System.Drawing.Color.Black,
            SizeMode = PictureBoxSizeMode.Zoom,
        };
        _mainSplit.Panel1.Controls.Add(_screen);

        // Floppy drive panel (below screen)
        BuildFloppyDrivePanel();
        _mainSplit.Panel1.Controls.Add(_drivePanel);

        // ── Right side: debug tabs ──
        BuildDebugPanel();

        // ── Status bar with activity LEDs and power button ──
        _statusBar = new StatusStrip();
        _statusLabel = new ToolStripStatusLabel("Ready - Load a COM/EXE file or start Shell");

        // Power button
        _powerButton = new ToolStripStatusLabel("⏻ Power On")
        {
            IsLink = true,
            ForeColor = System.Drawing.Color.LimeGreen,
            Font = new System.Drawing.Font("Segoe UI", 9, FontStyle.Bold),
            BorderSides = ToolStripStatusLabelBorderSides.All,
            BorderStyle = Border3DStyle.RaisedOuter,
            Padding = new Padding(4, 0, 4, 0),
        };
        _powerButton.Click += (_, _) => OnTogglePower();

        // HDD activity LED
        _hddLed = new ToolStripStatusLabel("● HDD")
        {
            ForeColor = System.Drawing.Color.DarkGray,
            Font = new System.Drawing.Font("Segoe UI", 8, FontStyle.Bold),
        };

        // CPU activity LED
        _cpuLed = new ToolStripStatusLabel("● CPU")
        {
            ForeColor = System.Drawing.Color.DarkGray,
            Font = new System.Drawing.Font("Segoe UI", 8, FontStyle.Bold),
        };

        _biosWaitLabel = new ToolStripStatusLabel("")
        {
            ForeColor = System.Drawing.Color.Gold,
            Font = new System.Drawing.Font("Segoe UI", 9, FontStyle.Bold)
        };
        _statusBar.Items.Add(_powerButton);
        _statusBar.Items.Add(new ToolStripSeparator());
        _statusBar.Items.Add(_statusLabel);
        _statusBar.Items.Add(new ToolStripStatusLabel { Spring = true });
        _statusBar.Items.Add(_hddLed);
        _statusBar.Items.Add(_cpuLed);
        _statusBar.Items.Add(new ToolStripSeparator());
        _statusBar.Items.Add(_biosWaitLabel);
        Controls.Add(_statusBar);

        // Initial splitter distance is applied in OnLoad once a window handle exists.
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        AdjustMainSplitterDistance();
    }

    private void AdjustMainSplitterDistance()
    {
        if (_adjustingMainSplit || _mainSplit == null || _mainSplit.IsDisposed)
            return;
        int width = _mainSplit.ClientSize.Width;
        if (width <= 0)
            return;

        _adjustingMainSplit = true;
        try
        {
            const int desiredLeftMin = 500;
            const int desiredRightMin = 250;

            // Keep constraints valid for current width.
            int leftMin = Math.Min(desiredLeftMin, width);
            int rightMin = Math.Min(desiredRightMin, Math.Max(0, width - leftMin));
            if (leftMin + rightMin > width)
                leftMin = Math.Max(0, width - rightMin);

            // Make current splitter safe before tightening panel min sizes.
            int safeCurrent = Math.Clamp(_mainSplit.SplitterDistance, 0, Math.Max(0, width - rightMin));
            if (_mainSplit.SplitterDistance != safeCurrent)
                _mainSplit.SplitterDistance = safeCurrent;

            _mainSplit.Panel1MinSize = leftMin;
            _mainSplit.Panel2MinSize = rightMin;

            int min = leftMin;
            int max = Math.Max(min, width - rightMin);
            int target = Math.Clamp(700, min, max);
            if (_mainSplit.SplitterDistance != target)
                _mainSplit.SplitterDistance = target;
        }
        catch (InvalidOperationException)
        {
            // Fallback during transient layout states.
            _mainSplit.Panel1MinSize = 0;
            _mainSplit.Panel2MinSize = 0;
            _mainSplit.SplitterDistance = Math.Max(0, width / 2);
        }
        finally
        {
            _adjustingMainSplit = false;
        }
    }

    private static ToolStripMenuItem MakeMenuItem(string text, Keys shortcut, EventHandler handler)
    {
        var item = new ToolStripMenuItem(text, null, handler);
        item.ShortcutKeys = shortcut;
        return item;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  FLOPPY DRIVE PANEL
    // ═══════════════════════════════════════════════════════════════════
    private void BuildFloppyDrivePanel()
    {
        _drivePanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 70,
            BackColor = System.Drawing.Color.FromArgb(40, 40, 60),
            Padding = new Padding(4),
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        _driveAPanel = BuildSingleDrivePanel('A', out _driveALabel, out _driveALed,
            out _driveAType, out _driveAInsert, out _driveAEject, out _driveASave, out _driveABlank);
        _driveBPanel = BuildSingleDrivePanel('B', out _driveBLabel, out _driveBLed,
            out _driveBType, out _driveBInsert, out _driveBEject, out _driveBSave, out _driveBBlank);

        table.Controls.Add(_driveAPanel, 0, 0);
        table.Controls.Add(_driveBPanel, 1, 0);
        _drivePanel.Controls.Add(table);
    }

    private static Panel BuildSingleDrivePanel(char letter, out Label label, out Panel led,
        out ComboBox typeCombo, out Button insertBtn, out Button ejectBtn, out Button saveBtn, out Button blankBtn)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = System.Drawing.Color.FromArgb(50, 50, 70),
            Margin = new Padding(2),
            Padding = new Padding(4),
        };

        // LED indicator
        led = new Panel
        {
            Size = new System.Drawing.Size(12, 12),
            Location = new System.Drawing.Point(6, 6),
            BackColor = System.Drawing.Color.DarkGray,
        };

        label = new Label
        {
            Text = $"  {letter}: [Empty]",
            ForeColor = System.Drawing.Color.LightGray,
            Location = new System.Drawing.Point(22, 4),
            AutoSize = true,
            Font = new System.Drawing.Font("Consolas", 9),
        };

        typeCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new System.Drawing.Point(6, 28),
            Width = 90,
            Font = new System.Drawing.Font("Segoe UI", 8),
        };
        typeCombo.Items.AddRange(new object[] { "1.44 MB", "720 KB", "1.2 MB", "360 KB" });
        typeCombo.SelectedIndex = 0;

        insertBtn = new Button
        {
            Text = "Insert",
            Location = new System.Drawing.Point(102, 26),
            Size = new System.Drawing.Size(56, 24),
            Font = new System.Drawing.Font("Segoe UI", 8),
            FlatStyle = FlatStyle.Flat,
            ForeColor = System.Drawing.Color.LightGreen,
            BackColor = System.Drawing.Color.FromArgb(40, 80, 40),
        };

        ejectBtn = new Button
        {
            Text = "Eject",
            Location = new System.Drawing.Point(162, 26),
            Size = new System.Drawing.Size(50, 24),
            Font = new System.Drawing.Font("Segoe UI", 8),
            FlatStyle = FlatStyle.Flat,
            ForeColor = System.Drawing.Color.Orange,
            BackColor = System.Drawing.Color.FromArgb(80, 60, 20),
            Enabled = false,
        };

        saveBtn = new Button
        {
            Text = "Save",
            Location = new System.Drawing.Point(216, 26),
            Size = new System.Drawing.Size(50, 24),
            Font = new System.Drawing.Font("Segoe UI", 8),
            FlatStyle = FlatStyle.Flat,
            ForeColor = System.Drawing.Color.LightBlue,
            BackColor = System.Drawing.Color.FromArgb(30, 50, 80),
            Enabled = false,
        };

        blankBtn = new Button
        {
            Text = "Blank",
            Location = new System.Drawing.Point(6, 54),
            Size = new System.Drawing.Size(56, 24),
            Font = new System.Drawing.Font("Segoe UI", 8),
            FlatStyle = FlatStyle.Flat,
            ForeColor = System.Drawing.Color.White,
            BackColor = System.Drawing.Color.FromArgb(60, 60, 80),
        };

        panel.Controls.AddRange(new Control[] { led, label, typeCombo, insertBtn, ejectBtn, saveBtn, blankBtn });
        return panel;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  DEBUG PANEL (right side)
    // ═══════════════════════════════════════════════════════════════════
    private void BuildDebugPanel()
    {
        // Debug toolbar
        _debugToolbar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 32,
            BackColor = System.Drawing.Color.FromArgb(45, 45, 65),
            Padding = new Padding(4, 4, 4, 0),
        };

        _manualClockCheck = new CheckBox
        {
            Text = "Manual Clock",
            ForeColor = System.Drawing.Color.LightGray,
            Location = new System.Drawing.Point(4, 6),
            AutoSize = true,
            Font = new System.Drawing.Font("Segoe UI", 8),
        };
        _manualClockCheck.CheckedChanged += (_, _) => ToggleManualClock();

        _stepButton = new Button
        {
            Text = "Step (F10)",
            Location = new System.Drawing.Point(110, 3),
            Size = new System.Drawing.Size(70, 24),
            Font = new System.Drawing.Font("Segoe UI", 8),
            FlatStyle = FlatStyle.Flat,
            ForeColor = System.Drawing.Color.White,
            BackColor = System.Drawing.Color.FromArgb(60, 60, 100),
            Enabled = false,
        };
        _stepButton.Click += (_, _) => DoStep();

        _traceCheck = new CheckBox
        {
            Text = "Trace",
            ForeColor = System.Drawing.Color.LightGray,
            Location = new System.Drawing.Point(188, 6),
            AutoSize = true,
            Font = new System.Drawing.Font("Segoe UI", 8),
        };
        _traceCheck.CheckedChanged += (_, _) => ToggleTrace();

        _logLevelFilter = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new System.Drawing.Point(248, 4),
            Width = 80,
            Font = new System.Drawing.Font("Segoe UI", 8),
        };
        _logLevelFilter.Items.AddRange(new object[] { "Trace", "Debug", "Info", "Warning", "Error" });
        _logLevelFilter.SelectedIndex = 2; // Info
        _logLevelFilter.SelectedIndexChanged += (_, _) =>
        {
            if (_machine != null)
                _machine.Log.MinLevel = (LogLevel)_logLevelFilter.SelectedIndex;
        };

        _debugToolbar.Controls.AddRange(new Control[]
            { _manualClockCheck, _stepButton, _traceCheck, _logLevelFilter });
        _mainSplit.Panel2.Controls.Add(_debugToolbar);

        // Tab control
        _debugTabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = new System.Drawing.Font("Segoe UI", 8),
        };

        // Log tab
        var logTab = new TabPage("Log");
        _logView = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = System.Drawing.Color.FromArgb(20, 20, 35),
            ForeColor = System.Drawing.Color.LightGreen,
            Font = new System.Drawing.Font("Consolas", 8),
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both,
        };
        logTab.Controls.Add(_logView);

        // Memory tab
        var memTab = new TabPage("Memory");
        _registersLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 44,
            ForeColor = System.Drawing.Color.Cyan,
            BackColor = System.Drawing.Color.FromArgb(20, 20, 35),
            Font = new System.Drawing.Font("Consolas", 8.5f),
            Text = "Registers: (load a program to see register state)",
            Padding = new Padding(4),
        };
        _memoryView = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = System.Drawing.Color.FromArgb(20, 20, 35),
            ForeColor = System.Drawing.Color.LightGray,
            Font = new System.Drawing.Font("Consolas", 8),
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both,
        };
        memTab.Controls.Add(_memoryView);
        memTab.Controls.Add(_registersLabel);

        _debugTabs.TabPages.Add(logTab);
        _debugTabs.TabPages.Add(memTab);
        _mainSplit.Panel2.Controls.Add(_debugTabs);
        _debugTabs.BringToFront();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  MACHINE INITIALIZATION
    // ═══════════════════════════════════════════════════════════════════
    private void InitializeMachine()
    {
        string dosRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MsDosEmulator");
        _streams = new WinFormsStreamProvider(dosRoot);
        _renderer = new WinFormsRenderer(_screen);
        _events = new WinFormsEventRegistry();
        _events.Attach(this);

        _machine = new DosMachine(_renderer, _events, _streams);

        // UI host clock pump: resumes pending BIOS waits when their timeout elapses.
        _uiWaitPumpTimer?.Stop();
        _uiWaitPumpTimer?.Dispose();
        _uiWaitPumpTimer = new System.Windows.Forms.Timer { Interval = 5 };
        _uiWaitPumpTimer.Tick += (_, _) =>
        {
            _machine?.PumpBiosWaitFromHostClock();
            _machine?.PumpActivityLeds();
            bool pending = _machine?.IsBiosWaitPending == true;
            _biosWaitLabel.Text = pending ? "BIOS WAIT…" : string.Empty;
        };
        _uiWaitPumpTimer.Start();

        // Wire activity LEDs
        _machine.OnDiskActivity += active =>
        {
            try { BeginInvoke(() => _hddLed.ForeColor = active ? System.Drawing.Color.Orange : System.Drawing.Color.DarkGray); } catch { }
        };
        _machine.OnCpuActivity += active =>
        {
            try { BeginInvoke(() => _cpuLed.ForeColor = active ? System.Drawing.Color.LimeGreen : System.Drawing.Color.DarkGray); } catch { }
        };
        _machine.OnPowerStateChanged += powered =>
        {
            try { BeginInvoke(() =>
            {
                _powerButton.Text = powered ? "⏻ Power Off" : "⏻ Power On";
                _powerButton.ForeColor = powered ? System.Drawing.Color.Red : System.Drawing.Color.LimeGreen;
            }); } catch { }
        };

        // Wire process exit
        _machine.OnProcessExit += code =>
        {
            BeginInvoke(() => _statusLabel.Text = $"Process exited with code {code}");
        };

        // Wire log entries to the log viewer
        _machine.Log.EntryAdded += entry =>
        {
            try
            {
                BeginInvoke(() =>
                {
                    if (entry.Level >= LogLevel.Warning)
                        _statusLabel.Text = entry.ToString();

                    AppendLogEntry(entry);
                });
            }
            catch { /* form may be closing */ }
        };

        // Wire manual step to update memory view
        _machine.OnManualStep += diff =>
        {
            try
            {
                BeginInvoke(() => UpdateMemoryDiffView(diff));
            }
            catch { /* form may be closing */ }
        };

        // Wire floppy drive activity LEDs
        WireFloppyDriveEvents();

        _machine.Reset();

        // Wire floppy insert/eject/save buttons
        _driveAInsert.Click += (_, _) => OnInsertFloppy('A');
        _driveBInsert.Click += (_, _) => OnInsertFloppy('B');
        _driveAEject.Click += (_, _) => OnEjectFloppy('A');
        _driveBEject.Click += (_, _) => OnEjectFloppy('B');
        _driveASave.Click += (_, _) => OnSaveFloppy('A');
        _driveBSave.Click += (_, _) => OnSaveFloppy('B');
        _driveABlank.Click += (_, _) => OnInsertBlankFloppy('A');
        _driveBBlank.Click += (_, _) => OnInsertBlankFloppy('B');

        _driveAType.SelectedIndexChanged += (_, _) => OnDriveTypeChanged('A', _driveAType);
        _driveBType.SelectedIndexChanged += (_, _) => OnDriveTypeChanged('B', _driveBType);
    }

    private void WireFloppyDriveEvents()
    {
        if (_machine == null) return;

        _machine.Floppy.SlotA.ActivityChanged += slot =>
        {
            try { BeginInvoke(() => UpdateDriveLed(_driveALed, slot)); } catch { }
        };
        _machine.Floppy.SlotB.ActivityChanged += slot =>
        {
            try { BeginInvoke(() => UpdateDriveLed(_driveBLed, slot)); } catch { }
        };
        _machine.Floppy.SlotA.DiskChanged += slot =>
        {
            try { BeginInvoke(() => UpdateDriveLabel('A', _driveALabel, _driveAEject, _driveASave, slot)); } catch { }
        };
        _machine.Floppy.SlotB.DiskChanged += slot =>
        {
            try { BeginInvoke(() => UpdateDriveLabel('B', _driveBLabel, _driveBEject, _driveBSave, slot)); } catch { }
        };
    }

    private static void UpdateDriveLed(Panel led, FloppyDriveSlot slot)
    {
        if (slot.IsWriting)
            led.BackColor = System.Drawing.Color.Red;
        else if (slot.IsReading)
            led.BackColor = System.Drawing.Color.LimeGreen;
        else
            led.BackColor = slot.HasDisk ? System.Drawing.Color.DarkGreen : System.Drawing.Color.DarkGray;
    }

    private static void UpdateDriveLabel(char letter, Label label, Button ejectBtn, Button saveBtn, FloppyDriveSlot slot)
    {
        label.Text = slot.HasDisk
            ? $"  {letter}: [{slot.DiskLabel}] {slot.DiskType}"
            : $"  {letter}: [Empty]";
        ejectBtn.Enabled = slot.HasDisk;
        saveBtn.Enabled = slot.HasDisk;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  LOG VIEWER
    // ═══════════════════════════════════════════════════════════════════
    private void AppendLogEntry(LogEntry entry)
    {
        var color = entry.Level switch
        {
            LogLevel.Trace => System.Drawing.Color.Gray,
            LogLevel.Debug => System.Drawing.Color.DarkGray,
            LogLevel.Info => System.Drawing.Color.LightGreen,
            LogLevel.Warning => System.Drawing.Color.Orange,
            LogLevel.Error => System.Drawing.Color.Red,
            _ => System.Drawing.Color.LightGray,
        };

        _logView.SelectionStart = _logView.TextLength;
        _logView.SelectionColor = color;
        _logView.AppendText(entry.ToString() + "\n");
        _logView.ScrollToCaret();

        // Limit log lines
        if (_logView.Lines.Length > 2000)
        {
            _logView.SelectionStart = 0;
            _logView.SelectionLength = _logView.GetFirstCharIndexFromLine(500);
            _logView.SelectedText = "";
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  MEMORY DIFF VIEW (git-diff style)
    // ═══════════════════════════════════════════════════════════════════
    private void UpdateMemoryDiffView(MemoryDiff diff)
    {
        // Update registers
        var rb = diff.RegistersBefore;
        var ra = diff.RegistersAfter;

        string RegDiff(string name, ushort before, ushort after) =>
            before != after ? $"{name}:{before:X4}→{after:X4}" : $"{name}:{after:X4}";

        _registersLabel.Text =
            $"{RegDiff("AX", rb.AX, ra.AX)}  {RegDiff("BX", rb.BX, ra.BX)}  " +
            $"{RegDiff("CX", rb.CX, ra.CX)}  {RegDiff("DX", rb.DX, ra.DX)}  " +
            $"{RegDiff("SI", rb.SI, ra.SI)}  {RegDiff("DI", rb.DI, ra.DI)}  " +
            $"{RegDiff("SP", rb.SP, ra.SP)}  {RegDiff("BP", rb.BP, ra.BP)}\n" +
            $"{RegDiff("CS", rb.CS, ra.CS)}  {RegDiff("DS", rb.DS, ra.DS)}  " +
            $"{RegDiff("ES", rb.ES, ra.ES)}  {RegDiff("SS", rb.SS, ra.SS)}  " +
            $"{RegDiff("IP", rb.IP, ra.IP)}  {RegDiff("FL", rb.Flags, ra.Flags)}";

        // Build memory hex dump with diff highlighting
        _memoryView.Clear();
        var before = diff.Before;
        var after = diff.After;
        var changedAddrs = new HashSet<uint>(diff.Changes.Select(c => c.Address));

        int bytesPerLine = 16;
        for (int offset = 0; offset < after.Length; offset += bytesPerLine)
        {
            uint addr = after.StartAddress + (uint)offset;

            // Address column
            _memoryView.SelectionColor = System.Drawing.Color.Cyan;
            _memoryView.AppendText($"{addr:X5}  ");

            // Hex bytes
            for (int i = 0; i < bytesPerLine && offset + i < after.Length; i++)
            {
                uint byteAddr = addr + (uint)i;
                byte val = after.Data[offset + i];

                if (changedAddrs.Contains(byteAddr))
                {
                    // Changed byte — highlight in red/green like git diff
                    byte oldVal = before.Data[offset + i];
                    _memoryView.SelectionBackColor = System.Drawing.Color.FromArgb(60, 20, 20);
                    _memoryView.SelectionColor = System.Drawing.Color.Red;
                    _memoryView.AppendText($"{oldVal:X2}");
                    _memoryView.SelectionBackColor = System.Drawing.Color.FromArgb(20, 60, 20);
                    _memoryView.SelectionColor = System.Drawing.Color.LimeGreen;
                    _memoryView.AppendText($"→{val:X2} ");
                    _memoryView.SelectionBackColor = System.Drawing.Color.FromArgb(20, 20, 35);
                }
                else
                {
                    _memoryView.SelectionColor = System.Drawing.Color.LightGray;
                    _memoryView.AppendText($"{val:X2} ");
                }
            }

            // ASCII representation
            _memoryView.SelectionColor = System.Drawing.Color.DarkGray;
            _memoryView.AppendText("  ");
            for (int i = 0; i < bytesPerLine && offset + i < after.Length; i++)
            {
                byte val = after.Data[offset + i];
                char c = (val >= 0x20 && val < 0x7F) ? (char)val : '.';
                uint byteAddr = addr + (uint)i;

                if (changedAddrs.Contains(byteAddr))
                    _memoryView.SelectionColor = System.Drawing.Color.Yellow;
                else
                    _memoryView.SelectionColor = System.Drawing.Color.DarkGray;

                _memoryView.AppendText(c.ToString());
            }

            _memoryView.AppendText("\n");
        }

        _memoryView.SelectionStart = 0;

        // Switch to memory tab
        _debugTabs.SelectedIndex = 1;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  MANUAL CLOCK / STEP CONTROLS
    // ═══════════════════════════════════════════════════════════════════
    private void ToggleManualClock()
    {
        if (_machine == null) return;
        _machine.ManualClockMode = !_machine.ManualClockMode;
        _stepButton.Enabled = _machine.ManualClockMode;

        // Keep checkbox in sync (may be called from menu)
        if (_manualClockCheck.Checked != _machine.ManualClockMode)
            _manualClockCheck.Checked = _machine.ManualClockMode;

        _statusLabel.Text = _machine.ManualClockMode
            ? "Manual clock mode — press F10 or Step to execute instructions"
            : "Automatic clock mode";
    }

    private void DoStep()
    {
        if (_machine == null || !_machine.ManualClockMode) return;
        _machine.StepInstruction();
    }

    private void ToggleTrace()
    {
        if (_machine == null) return;
        _machine.Log.TraceInstructions = !_machine.Log.TraceInstructions;
        if (_machine.Log.TraceInstructions)
            _machine.Log.MinLevel = LogLevel.Trace;

        if (_traceCheck.Checked != _machine.Log.TraceInstructions)
            _traceCheck.Checked = _machine.Log.TraceInstructions;

        _statusLabel.Text = _machine.Log.TraceInstructions
            ? "Instruction tracing enabled"
            : "Instruction tracing disabled";
    }

    // ═══════════════════════════════════════════════════════════════════
    //  FLOPPY DRIVE OPERATIONS
    // ═══════════════════════════════════════════════════════════════════
    private async void OnInsertFloppy(char drive)
    {
        using var dlg = new OpenFileDialog
        {
            Title = $"Insert Disk into {drive}:",
            Filter = "Disk Images|*.img;*.raw;*.ima;*.dsk|All Files|*.*",
            DefaultExt = "img"
        };

        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            byte[] data = await File.ReadAllBytesAsync(dlg.FileName);
            string label = Path.GetFileName(dlg.FileName);

            bool ok = _machine!.InsertFloppyDisk(drive, data, label);
            if (!ok)
            {
                MessageBox.Show($"Failed to load disk image into {drive}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            _statusLabel.Text = $"Disk inserted into {drive}: ({label})";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnEjectFloppy(char drive)
    {
        var data = _machine?.EjectFloppyDisk(drive);
        if (data != null && _machine!.Floppy.GetSlot(drive) is { IsDirty: true })
        {
            var result = MessageBox.Show($"Disk {drive}: was modified. Save before ejecting?",
                "Save Disk?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result == DialogResult.Yes)
                SaveFloppyToFile(drive, data);
        }
        _statusLabel.Text = $"Disk ejected from {drive}:";
    }

    private void OnSaveFloppy(char drive)
    {
        var data = _machine?.SaveFloppyState(drive);
        if (data != null)
            SaveFloppyToFile(drive, data);
    }

    private void OnInsertBlankFloppy(char drive)
    {
        if (_machine == null) return;

        // Get selected disk type from the appropriate combo
        var typeCombo = drive == 'A' ? _driveAType : _driveBType;
        var diskType = typeCombo.SelectedIndex switch
        {
            1 => MsDos.Core.Dos.FloppyDiskType.Floppy720K,
            2 => MsDos.Core.Dos.FloppyDiskType.Floppy1200K,
            3 => MsDos.Core.Dos.FloppyDiskType.Floppy360K,
            _ => MsDos.Core.Dos.FloppyDiskType.Floppy1440K,
        };

        bool ok = _machine.InsertBlankFloppyDisk(drive, diskType);
        if (ok)
            _statusLabel.Text = $"Blank {diskType} disk inserted into {drive}:";
        else
            MessageBox.Show($"Failed to create blank disk for {drive}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void SaveFloppyToFile(char drive, byte[] data)
    {
        using var dlg = new SaveFileDialog
        {
            Title = $"Save {drive}: Disk Image",
            Filter = "Disk Images|*.img|All Files|*.*",
            DefaultExt = "img",
            FileName = $"drive_{char.ToLower(drive)}.img"
        };

        if (dlg.ShowDialog() == DialogResult.OK)
        {
            File.WriteAllBytes(dlg.FileName, data);
            _statusLabel.Text = $"Drive {drive}: saved to {Path.GetFileName(dlg.FileName)}";
        }
    }

    private void OnDriveTypeChanged(char drive, ComboBox combo)
    {
        if (_machine == null) return;
        var diskType = combo.SelectedIndex switch
        {
            0 => FloppyDiskType.Floppy1440K,
            1 => FloppyDiskType.Floppy720K,
            2 => FloppyDiskType.Floppy1200K,
            3 => FloppyDiskType.Floppy360K,
            _ => FloppyDiskType.Floppy1440K,
        };
        _machine.Floppy.SetDiskType(drive, diskType);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  FILE / MACHINE OPERATIONS (existing, refactored)
    // ═══════════════════════════════════════════════════════════════════
    private async void OnLoadBinary(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Load DOS Binary",
            Filter = "DOS Executables|*.com;*.exe|All Files|*.*",
            DefaultExt = "com"
        };

        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            _cts?.Cancel();
            byte[] data = await File.ReadAllBytesAsync(dlg.FileName);
            string fileName = Path.GetFileName(dlg.FileName).ToUpperInvariant();
            await _streams!.ImportBinaryAsync(fileName, data);

            bool loaded = _machine!.LoadBinary(data);
            if (!loaded)
            {
                MessageBox.Show("Failed to load binary file. Unsupported format.", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _statusLabel.Text = $"Running: {fileName}";
            _cts = new CancellationTokenSource();
            _ = _machine.RunAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error loading file: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void OnLoadDiskImage(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Load Disk Image",
            Filter = "Disk Images|*.img;*.raw;*.ima;*.dsk|All Files|*.*",
            DefaultExt = "img"
        };

        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            byte[] data = await File.ReadAllBytesAsync(dlg.FileName);
            string fileName = Path.GetFileName(dlg.FileName);

            char drive = 'A';
            if (_machine!.GetDriveProvider('A') != null)
                drive = 'B';

            bool mounted = _machine.LoadDiskImage(data, drive);
            if (!mounted)
            {
                MessageBox.Show("Failed to load disk image. Invalid or unsupported format.",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _statusLabel.Text = $"Disk image mounted as {drive}: ({fileName})";

            if (!_machine.IsRunning)
            {
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                _ = _machine.RunShellAsync(_cts.Token);
                _statusLabel.Text = $"Shell running - Disk {drive}: loaded ({fileName})";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error loading disk image: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnReset(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        _machine?.Reset();

        // Show which drives are still attached after reset
        var drives = _machine?.GetMountedDrives();
        if (drives != null && drives.Count > 1) // More than just default C:
        {
            var driveList = string.Join(", ", drives.Select(c => $"{c}:"));
            _statusLabel.Text = $"Machine reset - Drives still attached: {driveList}";
        }
        else
        {
            _statusLabel.Text = "Machine reset - Load a COM or EXE file to start";
        }
    }

    private async void OnStartShell(object? sender, EventArgs e)
    {
        try
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _statusLabel.Text = "Shell running (COMMAND.COM)";
            _ = _machine!.RunShellAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error starting shell: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _uiWaitPumpTimer?.Stop();
        _uiWaitPumpTimer?.Dispose();
        _uiWaitPumpTimer = null;
        _cts?.Cancel();
        _machine?.Stop();
        _renderer?.Dispose();
        base.OnFormClosing(e);
    }

    private async void OnExportCDrive(object? sender, EventArgs e)
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Export C: Drive as Disk Image",
            Filter = "Disk Images|*.img|All Files|*.*",
            DefaultExt = "img",
            FileName = "drive_c.img"
        };

        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            _statusLabel.Text = "Exporting C: drive...";
            byte[] image = await MsDos.Core.Dos.DiskImageWriter.ExportDriveAsImageAsync(_streams!, _machine!.Log);
            await File.WriteAllBytesAsync(dlg.FileName, image);
            _statusLabel.Text = $"C: drive exported to {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error exporting drive: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void OnImportCDrive(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Import C: Drive from Disk Image",
            Filter = "Disk Images|*.img;*.raw;*.ima;*.dsk|All Files|*.*",
            DefaultExt = "img"
        };

        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            byte[] data = await File.ReadAllBytesAsync(dlg.FileName);
            bool mounted = _machine!.MountDiskImage('C', data);
            if (!mounted)
            {
                MessageBox.Show("Failed to load disk image as C: drive.",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            _statusLabel.Text = $"C: drive loaded from {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error importing drive: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  POWER ON / OFF
    // ═══════════════════════════════════════════════════════════════════
    private async void OnTogglePower()
    {
        if (_machine == null) return;

        if (_machine.IsPoweredOn)
        {
            _cts?.Cancel();
            _machine.PowerOff();
            _statusLabel.Text = "Power OFF — insert a floppy and press Power On";
        }
        else
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _statusLabel.Text = "Power ON — booting...";
            _ = _machine.PowerOnAsync(_cts.Token);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  FILE UPLOAD / DOWNLOAD
    // ═══════════════════════════════════════════════════════════════════
    private async void OnUploadFile(object? sender, EventArgs e)
    {
        if (_machine == null) return;

        using var dlg = new OpenFileDialog
        {
            Title = "Upload File to Drive",
            Filter = "All Files|*.*",
        };

        if (dlg.ShowDialog() != DialogResult.OK) return;

        // Ask which drive
        char drive = PickDrive("Upload to which drive?");
        if (drive == '\0') return;

        try
        {
            byte[] data = await File.ReadAllBytesAsync(dlg.FileName);
            string filename = Path.GetFileName(dlg.FileName);
            string? dosName = await _machine.UploadFileToDriveAsync(drive, filename, data);
            if (dosName != null)
                _statusLabel.Text = $"Uploaded '{filename}' → '{dosName}' to {drive}:";
            else
                MessageBox.Show($"Failed to upload file to drive {drive}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error uploading file: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void OnDownloadFile(object? sender, EventArgs e)
    {
        if (_machine == null) return;

        // Ask which drive
        char drive = PickDrive("Download from which drive?");
        if (drive == '\0') return;

        // List files on the drive
        var files = await _machine.ListDriveFilesAsync(drive);
        if (files == null || files.Count == 0)
        {
            MessageBox.Show($"No files found on {drive}:", "Info",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Let user pick a file
        string? filename = PickFile(files, $"Select file from {drive}:");
        if (filename == null) return;

        try
        {
            byte[]? data = await _machine.DownloadFileFromDriveAsync(drive, filename);
            if (data == null)
            {
                MessageBox.Show($"Could not read '{filename}' from {drive}:", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            using var dlg = new SaveFileDialog
            {
                Title = $"Save {filename}",
                FileName = filename,
                Filter = "All Files|*.*",
            };

            if (dlg.ShowDialog() == DialogResult.OK)
            {
                await File.WriteAllBytesAsync(dlg.FileName, data);
                _statusLabel.Text = $"Downloaded '{filename}' from {drive}: ({data.Length} bytes)";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error downloading file: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Show a simple dialog to pick a drive letter from mounted drives.</summary>
    private char PickDrive(string prompt)
    {
        var drives = _machine?.GetMountedDrives();
        if (drives == null || drives.Count == 0)
        {
            MessageBox.Show("No drives mounted.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return '\0';
        }

        using var form = new Form
        {
            Text = prompt,
            ClientSize = new System.Drawing.Size(250, 100),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
        };

        var combo = new ComboBox
        {
            Location = new System.Drawing.Point(20, 20),
            Width = 200,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        foreach (var d in drives) combo.Items.Add($"{d}:");
        combo.SelectedIndex = 0;

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new System.Drawing.Point(80, 60) };
        form.Controls.AddRange(new Control[] { combo, ok });
        form.AcceptButton = ok;

        return form.ShowDialog() == DialogResult.OK && combo.SelectedItem is string s && s.Length >= 1
            ? s[0]
            : '\0';
    }

    /// <summary>Show a simple dialog to pick a file from a list.</summary>
    private static string? PickFile(IReadOnlyList<string> files, string title)
    {
        using var form = new Form
        {
            Text = title,
            ClientSize = new System.Drawing.Size(300, 300),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
        };

        var listBox = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = new System.Drawing.Font("Consolas", 10),
        };
        foreach (var f in files) listBox.Items.Add(f);
        listBox.SelectedIndex = 0;

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom };
        form.Controls.Add(listBox);
        form.Controls.Add(ok);
        form.AcceptButton = ok;
        listBox.DoubleClick += (_, _) => { form.DialogResult = DialogResult.OK; form.Close(); };

        return form.ShowDialog() == DialogResult.OK && listBox.SelectedItem is string s ? s : null;
    }
}
