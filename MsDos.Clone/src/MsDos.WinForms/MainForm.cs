using MsDos.Core;
using MsDos.WinForms.Platform;

namespace MsDos.WinForms;

public partial class MainForm : Form
{
    private DosMachine? _machine;
    private WinFormsRenderer? _renderer;
    private WinFormsEventRegistry? _events;
    private WinFormsStreamProvider? _streams;
    private CancellationTokenSource? _cts;

    private PictureBox _screen = null!;
    private MenuStrip _menu = null!;
    private StatusStrip _statusBar = null!;
    private ToolStripStatusLabel _statusLabel = null!;

    public MainForm()
    {
        InitializeComponent();
        SetupUI();
        InitializeMachine();
    }

    private void SetupUI()
    {
        Text = "MS-DOS Emulator (.NET)";
        ClientSize = new System.Drawing.Size(800, 560);
        BackColor = System.Drawing.Color.Black;
        KeyPreview = true;

        // Menu bar
        _menu = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("&File");
        var loadComItem = new ToolStripMenuItem("&Load Binary (COM/EXE)...", null, OnLoadBinary);
        loadComItem.ShortcutKeys = Keys.Control | Keys.O;
        var shellItem = new ToolStripMenuItem("Start &Shell (COMMAND.COM)", null, OnStartShell);
        shellItem.ShortcutKeys = Keys.Control | Keys.S;
        var resetItem = new ToolStripMenuItem("&Reset Machine", null, OnReset);
        resetItem.ShortcutKeys = Keys.Control | Keys.R;
        var exitItem = new ToolStripMenuItem("E&xit", null, (_, _) => Close());
        exitItem.ShortcutKeys = Keys.Alt | Keys.F4;
        fileMenu.DropDownItems.AddRange(new ToolStripItem[] { loadComItem, shellItem, new ToolStripSeparator(), resetItem, new ToolStripSeparator(), exitItem });
        _menu.Items.Add(fileMenu);
        MainMenuStrip = _menu;
        Controls.Add(_menu);

        // Screen (PictureBox)
        _screen = new PictureBox
        {
            Dock = DockStyle.Fill,
            BackColor = System.Drawing.Color.Black,
            SizeMode = PictureBoxSizeMode.Zoom,
        };
        Controls.Add(_screen);
        _screen.BringToFront();

        // Status bar
        _statusBar = new StatusStrip();
        _statusLabel = new ToolStripStatusLabel("Ready - Load a COM or EXE file to start");
        _statusBar.Items.Add(_statusLabel);
        Controls.Add(_statusBar);
    }

    private void InitializeMachine()
    {
        string dosRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MsDosEmulator");
        _streams = new WinFormsStreamProvider(dosRoot);
        _renderer = new WinFormsRenderer(_screen);
        _events = new WinFormsEventRegistry();
        _events.Attach(this);

        _machine = new DosMachine(_renderer, _events, _streams);
        _machine.OnProcessExit += code =>
        {
            BeginInvoke(() =>
            {
                _statusLabel.Text = $"Process exited with code {code}";
            });
        };
        _machine.Reset();
    }

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

            // Import the binary into the stream provider
            await _streams!.ImportBinaryAsync(fileName, data);

            // Load into machine
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

    private void OnReset(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        _machine?.Reset();
        _statusLabel.Text = "Machine reset - Load a COM or EXE file to start";
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
        _cts?.Cancel();
        _machine?.Stop();
        _renderer?.Dispose();
        base.OnFormClosing(e);
    }
}

