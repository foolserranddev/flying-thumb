using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;

namespace FlyingThumbManager;

public sealed class MainForm : Form
{
    sealed record UploadItem(string LocalPath, string RemotePath);
    readonly BindingList<Device> devices = [];
    readonly FlyingThumbClient client = new();
    readonly DataGridView deviceGrid = new();
    readonly DataGridView fileGrid = new();

    readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    readonly ToolStripStatusLabel summary = new("Ready");
    readonly ToolStripStatusLabel transferDetail = new() { Visible = false, AutoSize = false, Width = 420, TextAlign = ContentAlignment.MiddleRight };
    readonly ToolStripProgressBar transferProgress = new() { Minimum = 0, Maximum = 1000, Value = 0, Width = 190, Visible = false };
    readonly ToolStripButton cancelTransfer = new("Cancel transfer") { Visible = false };
    CancellationTokenSource? transferCancellation;
    bool transferWasCancelled;
    readonly Stopwatch transferClock = new();
    readonly string sessionLogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlyingThumb", "Logs", $"manager-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    readonly Button addButton = new() { Text = "Add files", AutoSize = true };
    readonly Button syncButton = new() { Text = "Sync...", AutoSize = true };
    readonly Button refreshFilesButton = new() { Text = "Refresh", AutoSize = true };
    readonly Button deleteFilesButton = new() { Text = "Delete", AutoSize = true, Enabled = false };
    readonly Button updateNowButton = new() { Text = "Update Now", AutoSize = true };
    readonly Label updateNotice = new() { AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(8, 8, 12, 8) };
    readonly FlowLayoutPanel updateBanner = new() { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Visible = false, BackColor = Color.FromArgb(255, 244, 204), Padding = new Padding(8, 4, 8, 4), Margin = new Padding(10, 2, 10, 6) };
    readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    readonly SplitContainer split = new();
    readonly System.Windows.Forms.Timer setupNetworkTimer = new() { Interval = 1500 };
    readonly Dictionary<string, List<RemoteFile>> inventories = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> checkedFiles = new(StringComparer.OrdinalIgnoreCase);
    CheckBoxHeaderCell? fileCheckHeader;
    bool renderingFileMatrix;
    bool busy;
    bool adjustingSplit;
    bool setupPageOpened;
    UpdateManifest? availableUpdate;
    string managementKey = ManagerSettings.LoadKey();
    readonly object transferProgressGate = new();
    readonly Dictionary<string, long> activeTransferBytes = new();
    long transferCompletedBytes;
    long transferTotalBytes;
    int transferCompletedOperations;
    int transferTotalOperations;

    string? automatedDiagnosticReport;
    public bool AutomatedDiagnosticSucceeded { get; private set; }
    public MainForm(bool diagnosticWorker = false)
    {
        Text = "Flying Thumb Manager";
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(760, 520);
        var desktop = Screen.FromPoint(Cursor.Position).WorkingArea;
        var virtualDesktop = SystemInformation.VirtualScreen;
        var savedWindow = ManagerSettings.LoadWindowSize();
        var desired = savedWindow?.Size ?? new Size(1240 * 2, (int)Math.Round(820 * 1.5));
        Size = new Size(
            Math.Clamp(desired.Width, MinimumSize.Width, Math.Max(MinimumSize.Width, virtualDesktop.Width - 64)),
            Math.Clamp(desired.Height, MinimumSize.Height, Math.Max(MinimumSize.Height, desktop.Height - 64)));
        StartPosition = FormStartPosition.CenterScreen;
        if (savedWindow?.Maximized == true) WindowState = FormWindowState.Maximized;
        Font = new Font("Segoe UI", 10);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        AllowDrop = true;
        BackColor = Color.FromArgb(243, 243, 243);

        var menu = BuildMenu();
        var dropHint = new Label
        {
            Text = "  Flying Thumb  >  Included drives  >  Files        Drop files or folders here to add them",
            AutoSize = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(65, 65, 65),
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(8, 6, 8, 6),
            Margin = new Padding(0, 0, 0, 6),
            AutoEllipsis = true
        };
        var buttonFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 0, 4)
        };
        foreach (var button in new[] { addButton, syncButton, refreshFilesButton, deleteFilesButton })
        {
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.FlatStyle = FlatStyle.System;
            button.Padding = new Padding(8, 2, 8, 2);
            button.Margin = new Padding(0, 0, 6, 0);
        }
        buttonFlow.Controls.AddRange([addButton, syncButton, refreshFilesButton, deleteFilesButton]);
        updateNowButton.Margin = new Padding(0, 2, 0, 2);
        updateBanner.Controls.Add(updateNotice);
        updateBanner.Controls.Add(updateNowButton);

        ConfigureDeviceGrid();
        ConfigureFileGrid();
        var fileTab = new TabPage("Files across all drives") { Padding = new Padding(4), BackColor = Color.White };
        fileTab.Controls.Add(fileGrid);
        var activityTab = new TabPage("Activity log") { Padding = new Padding(4), BackColor = Color.White };
        activityTab.Controls.Add(log);
        tabs.TabPages.Add(fileTab); tabs.TabPages.Add(activityTab);
        var fileArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0), Padding = new Padding(0) };
        fileArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fileArea.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fileArea.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fileArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        fileArea.Controls.Add(buttonFlow, 0, 1);
        fileArea.Controls.Add(dropHint, 0, 0);
        fileArea.Controls.Add(tabs, 0, 2);

        split.Dock = DockStyle.Fill; split.Orientation = Orientation.Horizontal; split.SplitterWidth = 6;
        split.Panel1.Padding = new Padding(10, 0, 10, 5); split.Panel1.Controls.Add(deviceGrid);
        split.Panel2.Padding = new Padding(10, 5, 10, 5); split.Panel2.Controls.Add(fileArea);
        summary.Spring = true; summary.TextAlign = ContentAlignment.MiddleLeft;
        var status = new StatusStrip { BackColor = Color.FromArgb(245, 245, 245), SizingGrip = true }; status.Items.AddRange([summary, transferDetail, transferProgress, cancelTransfer]);
        cancelTransfer.Click += (_, _) => { transferWasCancelled = true; transferCancellation?.Cancel(); cancelTransfer.Enabled = false; summary.Text = "Cancelling transfer; finishing drive cleanup..."; WriteLog("User requested transfer cancellation. Already confirmed files are retained."); };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(menu, 0, 0); root.Controls.Add(updateBanner, 0, 1); root.Controls.Add(split, 0, 2); root.Controls.Add(status, 0, 3);
        Controls.Add(root); MainMenuStrip = menu;

        addButton.Click += async (_, _) => await ChooseAndAddFiles();
        syncButton.Click += async (_, _) => await ChooseAndSync();
        refreshFilesButton.Click += async (_, _) => await RefreshFileMatrix();
        deleteFilesButton.Click += async (_, _) => await DeleteSelectedFiles();
        updateNowButton.Click += async (_, _) => await InstallAvailableUpdates();
        DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += async (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) await AddPaths(paths); };
        split.SizeChanged += (_, _) => ApplyResponsiveSplit();
        setupNetworkTimer.Tick += (_, _) => CheckForSetupNetwork();
        Shown += async (_, _) => { if (diagnosticWorker) return; WriteLog($"Flying Thumb Manager {UpdateService.CurrentManagerVersion} started."); await RefreshDevices(); ApplyResponsiveSplit(); setupNetworkTimer.Start(); CheckForSetupNetwork(); };
        FormClosing += (_, _) => { if (!diagnosticWorker) ManagerSettings.SaveWindowSize(WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size, WindowState == FormWindowState.Maximized); };
        FormClosed += (_, _) => setupNetworkTimer.Stop();
    }

    static bool IsSetupNetworkConnected()
    {
        return NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
            adapter.OperationalStatus == OperationalStatus.Up &&
            adapter.GetIPProperties().UnicastAddresses.Any(address =>
                address.Address.AddressFamily == AddressFamily.InterNetwork &&
                address.Address.ToString().StartsWith("192.168.77.", StringComparison.Ordinal)));
    }

    void CheckForSetupNetwork()
    {
        if (!IsSetupNetworkConnected()) { setupPageOpened = false; return; }
        if (setupPageOpened) return;
        setupPageOpened = true;
        OpenSetupHotspotPage();
    }

    void OpenSetupHotspotPage()
    {
        OpenSettingsPage("http://192.168.77.1/", "Flying Thumb Setup");
    }

    void OpenSelectedDriveSettings()
    {
        if (deviceGrid.CurrentRow?.DataBoundItem is not Device drive || drive.IsSimulated)
        {
            MessageBox.Show(this, "Choose one network-connected Flying Thumb Drive first.", "Drive Settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        OpenSettingsPage(new Uri(drive.BaseUri, "settings.html").ToString(), $"{drive.Name} settings");
    }

    void OpenSettingsPage(string address, string description)
    {
        try
        {
            Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
            summary.Text = description + " opened in your browser";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open the settings page. Open {address} in your browser.\n\n" + ex.Message, "Flying Thumb Settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    void EditShopKey(object? sender, EventArgs e)
    {
        var entered = Prompt.ShowSecret("Shop management key", "Flying Thumb Settings", managementKey);
        if (entered is null) return;
        managementKey = entered;
        ManagerSettings.SaveKey(managementKey);
        summary.Text = managementKey.Length == 0 ? "Shop management key cleared" : "Shop management key saved securely for this Windows account";
    }

    bool EnsureStorageAvailable(Device[] targets)
    {
        var unavailable = targets.Where(device => !device.IsSimulated && !device.StorageReady).Select(device => device.Name).ToArray();
        if (unavailable.Length == 0) return true;
        MessageBox.Show(this, "TF card unavailable on: " + string.Join(", ", unavailable) + ".\n\nCheck that the card is inserted, then restart the drive and refresh drives.", "Sync Cannot Start", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }
    bool EnsureManagementKey(Device[] targets)
    {
        if (!targets.Any(device => !device.IsSimulated && device.Claimed) || managementKey.Length > 0) return true;
        var entered = Prompt.ShowSecret("This drive is secured. Enter the shop management key", "Shop Management Key Required", "");
        if (entered is null) return false;
        if (string.IsNullOrWhiteSpace(entered))
        {
            MessageBox.Show(this, "Enter the shop management key to manage secured drives.", "Shop Management Key", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        managementKey = entered;
        ManagerSettings.SaveKey(managementKey);
        return true;
    }
    void ApplyResponsiveSplit()
    {
        if (adjustingSplit || !split.IsHandleCreated) return;
        const int deviceFloor = 110;
        const int fileFloor = 220;
        var available = split.ClientSize.Height;
        if (available < deviceFloor + fileFloor + split.SplitterWidth) return;

        adjustingSplit = true;
        try
        {
            var preferred = Math.Clamp((int)Math.Round(available * 0.38), deviceFloor, available - fileFloor - split.SplitterWidth);
            split.Panel1MinSize = Math.Min(deviceFloor, preferred);
            split.Panel2MinSize = Math.Min(fileFloor, available - preferred - split.SplitterWidth);
            if (Math.Abs(split.SplitterDistance - preferred) > 1)
                split.SplitterDistance = preferred;
        }
        finally { adjustingSplit = false; }
    }

    MenuStrip BuildMenu()
    {
        var menu = new MenuStrip { BackColor = Color.White, RenderMode = ToolStripRenderMode.System, Padding = new Padding(8, 3, 0, 3) };
        var file = new ToolStripMenuItem("File");
        file.DropDownItems.Add(Item("Add Files...", async (_, _) => await ChooseAndAddFiles(), Keys.Control | Keys.O));
        file.DropDownItems.Add(Item("Add Folder...", async (_, _) => await ChooseAndAddFolder()));
        file.DropDownItems.Add(Item("Sync Selected Files", async (_, _) => await ChooseAndSync(), Keys.Control | Keys.Shift | Keys.S));
        file.DropDownItems.Add(Item("Delete Selected Files...", async (_, _) => await DeleteSelectedFiles()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("Exit", (_, _) => Close()));

        var drives = new ToolStripMenuItem("Drives");
        drives.DropDownItems.Add(Item("Find Drives", async (_, _) => await RefreshDevices(), Keys.F5));
        drives.DropDownItems.Add(Item("Refresh Drive Contents", async (_, _) => await RefreshFileMatrix(), Keys.Control | Keys.R));
        drives.DropDownItems.Add(new ToolStripSeparator());
        drives.DropDownItems.Add(Item("Select All Drives", (_, _) => SelectAll(true)));
        drives.DropDownItems.Add(Item("Select No Drives", (_, _) => SelectAll(false)));
        drives.DropDownItems.Add(new ToolStripSeparator());
        drives.DropDownItems.Add(Item("Open Selected Drive's Settings Page", (_, _) => OpenSelectedDriveSettings()));
        drives.DropDownItems.Add(Item("Rename Selected Drive...", RenameDevice));
        drives.DropDownItems.Add(Item("Return Selected Drives to Writable USB Mode...", async (_, _) => await ReleaseManagedUsb()));
        drives.DropDownItems.Add(new ToolStripSeparator());
        drives.DropDownItems.Add(Item("Install / Recover a Drive via USB...", RecoverUsb));
        drives.DropDownItems.Add(Item("Diagnose a Drive over USB...", DiagnoseUsb));

        var settings = new ToolStripMenuItem("Settings");
        settings.DropDownItems.Add(Item("Shop Management Key...", EditShopKey));

        var help = new ToolStripMenuItem("Help");
        help.DropDownItems.Add(Item("Check for Updates...", async (_, _) => await CheckForUpdates(true)));
        help.DropDownItems.Add(Item("View Diagnostic Log...", (_, _) => { WriteLog("Diagnostic log opened."); ShowDiagnosticReport("Manager activity and transfer log", File.ReadAllText(sessionLogPath), false); }));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(Item("About Flying Thumb Manager", (_, _) => ShowAbout()));

        menu.Items.AddRange([file, drives, settings, help]);
        return menu;
    }

    void ShowAbout()
    {
        MessageBox.Show(this,
            $"Flying Thumb Manager\nVersion {UpdateService.CurrentManagerVersion}\n\nManage, synchronize, and update Flying Thumb drives over your shop network.",
            "About Flying Thumb Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    static ToolStripMenuItem Item(string text, EventHandler click, Keys shortcut = Keys.None)
    { var item = new ToolStripMenuItem(text); item.Click += click; if (shortcut != Keys.None) item.ShortcutKeys = shortcut; return item; }

    static void ApplyExplorerGridStyle(DataGridView grid)
    {
        grid.BackgroundColor = Color.White;
        grid.BorderStyle = BorderStyle.FixedSingle;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Color.FromArgb(232, 232, 232);
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(250, 250, 250);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(32, 32, 32);
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(250, 250, 250);
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.FromArgb(32, 32, 32);
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(5, 5, 5, 5);
        grid.ColumnHeadersHeight = 44;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.DefaultCellStyle.BackColor = Color.White;
        grid.DefaultCellStyle.ForeColor = Color.FromArgb(32, 32, 32);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(204, 232, 255);
        grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        grid.DefaultCellStyle.Padding = new Padding(5, 2, 5, 2);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(252, 252, 252);
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCellsExceptHeaders;
        grid.RowTemplate.MinimumHeight = 28;
        grid.ShowCellToolTips = true;
    }
    void ConfigureDeviceGrid()
    {
        deviceGrid.Dock = DockStyle.Fill; deviceGrid.AutoGenerateColumns = false; deviceGrid.AllowUserToAddRows = false; deviceGrid.AllowUserToDeleteRows = false;
        deviceGrid.MultiSelect = false; deviceGrid.RowHeadersVisible = false; deviceGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; deviceGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        deviceGrid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = nameof(Device.Selected), HeaderText = "Include", FillWeight = 45, MinimumWidth = 78 });
        deviceGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(Device.Name), HeaderText = "Machine", ReadOnly = true, FillWeight = 120, MinimumWidth = 160 });
        deviceGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(Device.Ip), HeaderText = "Address", ReadOnly = true, FillWeight = 80, MinimumWidth = 135 });
        deviceGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(Device.Firmware), HeaderText = "Firmware", ReadOnly = true, FillWeight = 55, MinimumWidth = 95 });
        deviceGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(Device.Free), HeaderText = "Free", ReadOnly = true, FillWeight = 55, MinimumWidth = 90 });
        deviceGrid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = nameof(Device.Claimed), HeaderText = "Secured", ReadOnly = true, FillWeight = 45, MinimumWidth = 92 });
        deviceGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(Device.Status), HeaderText = "Status", ReadOnly = true, FillWeight = 140, MinimumWidth = 150 });
        ApplyExplorerGridStyle(deviceGrid);
        deviceGrid.DataSource = devices;
        deviceGrid.CurrentCellDirtyStateChanged += (_, _) => { if (deviceGrid.IsCurrentCellDirty && deviceGrid.CurrentCell?.ColumnIndex == 0) deviceGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        deviceGrid.CellValueChanged += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 0 && !busy) BeginInvoke(RenderFileMatrix); };
    }

    void ConfigureFileGrid()
    {
        fileGrid.Dock = DockStyle.Fill; fileGrid.AllowUserToAddRows = false; fileGrid.AllowUserToDeleteRows = false; fileGrid.ReadOnly = false;
        fileGrid.MultiSelect = true; fileGrid.RowHeadersVisible = false; fileGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; fileGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; fileGrid.EditMode = DataGridViewEditMode.EditOnEnter;
        ApplyExplorerGridStyle(fileGrid);
        var menu = new ContextMenuStrip();
        var syncFiles = new ToolStripMenuItem("Sync selected files...");
        var delete = new ToolStripMenuItem("Delete") { ShortcutKeyDisplayString = "Del" };
        syncFiles.Click += async (_, _) => await SyncSelectedFiles();
        delete.Click += async (_, _) => await DeleteSelectedFiles();
        menu.Items.Add(syncFiles); menu.Items.Add(new ToolStripSeparator()); menu.Items.Add(delete);
        menu.Opening += (_, e) => { var count = ChosenFileNames().Length; syncFiles.Text = count == 1 ? "Sync this file..." : "Sync selected files..."; syncFiles.Enabled = delete.Enabled = !busy && count > 0; e.Cancel = count == 0; };
        fileGrid.ContextMenuStrip = menu;
        fileGrid.CurrentCellDirtyStateChanged += (_, _) => { if (fileGrid.IsCurrentCellDirty && fileGrid.CurrentCell?.OwningColumn.Name == "FileChecked") fileGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        fileGrid.CellValueChanged += (_, e) =>
        {
            if (renderingFileMatrix || e.RowIndex < 0 || e.ColumnIndex < 0 || e.ColumnIndex >= fileGrid.Columns.Count || fileGrid.Columns[e.ColumnIndex].Name != "FileChecked") return;
            var row = fileGrid.Rows[e.RowIndex]; var name = row.Cells["FileName"].Value?.ToString(); if (string.IsNullOrWhiteSpace(name)) return;
            if (row.Cells["FileChecked"].Value is true) checkedFiles.Add(name); else checkedFiles.Remove(name);
            UpdateFileCheckHeader();
            UpdateFileActionButtons();
            summary.Text = checkedFiles.Count == 0 ? "No files checked; Sync applies to all files" : $"{checkedFiles.Count} file(s) checked for file actions";
        };
        fileGrid.CellMouseDown += (_, e) => { if (e.Button == MouseButtons.Right && e.RowIndex >= 0) { if (checkedFiles.Count == 0 && !fileGrid.Rows[e.RowIndex].Selected) { fileGrid.ClearSelection(); fileGrid.Rows[e.RowIndex].Selected = true; } fileGrid.CurrentCell = fileGrid.Rows[e.RowIndex].Cells["FileName"]; } };
        fileGrid.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Delete && !busy) { e.Handled = true; e.SuppressKeyPress = true; await DeleteSelectedFiles(); } };
        fileGrid.SelectionChanged += (_, _) => UpdateFileActionButtons();
    }

    static bool FirmwareAtLeast(Device device, int major, int minor, int build)
    {
        var clean = device.Firmware.Split('-', 2)[0];
        return Version.TryParse(clean, out var version) && version >= new Version(major, minor, build);
    }
    static bool UsesManagedUsb(Device device) => FirmwareAtLeast(device, 2, 2, 0);
    static bool SupportsUsbRelease(Device device) => FirmwareAtLeast(device, 2, 3, 0);
    static bool SupportsFolders(Device device) => device.IsSimulated || FirmwareAtLeast(device, 2, 5, 0);
    static string ReadyStatus(Device device) => device.IsSimulated ? "Simulated" : device.UsbManaged ? "Manager controls files - USB read-only" : "Ready";
    bool EnsureManagedFirmware(Device[] targets)
    {
        var outdated = targets.Where(device => !device.IsSimulated && !UsesManagedUsb(device)).Select(device => device.Name).ToArray();
        if (outdated.Length == 0) return true;
        MessageBox.Show(this,
            "Update required before changing files on: " + string.Join(", ", outdated) + ".\n\nFile changes are disabled on older firmware to protect the TF card while USB is attached. Choose File > Check for Updates, install the drive update, then try again.",
            "Drive Update Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }
    bool EnsureFolderFirmware(Device[] targets)
    {
        var outdated = targets.Where(device => !SupportsFolders(device)).Select(device => device.Name).ToArray();
        if (outdated.Length == 0) return true;
        MessageBox.Show(this,
            "A drive update is required before using folders on: " + string.Join(", ", outdated) + ".\n\nChoose Help > Check for Updates, install the drive update, then try again. This prevents older firmware from flattening nested paths into the root folder.",
            "Drive Update Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }
    bool ConfirmManagedUsb(Device[] targets)
    {
        var switching = targets.Count(device => !device.IsSimulated && UsesManagedUsb(device) && !device.UsbManaged);
        if (switching == 0) return true;
        var noun = switching == 1 ? "drive" : "drives";
        return MessageBox.Show(this,
            $"Flying Thumb Manager will briefly pause USB access on {switching} {noun} while it changes files. Each drive will reconnect to its attached machine as writable automatically when the complete batch finishes.\n\nClose any files currently being written through USB before continuing.",
            "Begin Managed File Session", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK;
    }
    async Task ReleaseManagedUsb()
    {
        if (busy) return;
        var targets = SelectedDevices().Where(device => !device.IsSimulated && device.UsbManaged).ToArray();
        if (targets.Length == 0)
        {
            MessageBox.Show(this, "None of the included drives are currently in Manager-controlled read-only mode.", "USB Already Writable", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var outdated = targets.Where(device => !SupportsUsbRelease(device)).Select(device => device.Name).ToArray();
        if (outdated.Length > 0)
        {
            MessageBox.Show(this, "A drive update is required before writable USB can be restored without unplugging: " + string.Join(", ", outdated) + ".\n\nChoose File > Check for Updates, install the drive update, then try again.", "Drive Update Required", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var noun = targets.Length == 1 ? "drive" : "drives";
        if (MessageBox.Show(this, $"Return {targets.Length} included {noun} to normal writable USB mode?\n\nClose any files currently open from the Flying Thumb on the attached machine. Its USB disk will briefly disappear and reconnect as writable. Newer firmware normally does this automatically after every completed Manager operation.", "Return USB to Writable Mode", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;

        var succeeded = await RunForDevices(targets, async device =>
        {
            SetStatus(device, "Returning USB to writable mode...");
            await client.ReleaseManagedUsbAsync(device, Key);
            device.UsbManaged = false;
            return "USB writable";
        });
        summary.Text = succeeded ? $"USB is writable on {targets.Length} {noun}" : "One or more drives could not return to writable USB mode";
        deviceGrid.Refresh();
    }
    string Key => managementKey;
    Device[] SelectedDevices() { deviceGrid.EndEdit(); return devices.Where(x => x.Selected).ToArray(); }
    string[] CheckedFileNames()
    {
        fileGrid.EndEdit();
        return fileGrid.Rows.Cast<DataGridViewRow>().Where(row => row.Cells["FileChecked"].Value is true)
            .Select(row => row.Cells["FileName"].Value?.ToString()).Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => FlyingThumbClient.NormalizeRemotePath(name!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    string[] ChosenFileNames()
    {
        var checkedNames = CheckedFileNames();
        if (checkedNames.Length > 0) return checkedNames;
        return fileGrid.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Cells["FileName"].Value?.ToString())
            .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => FlyingThumbClient.NormalizeRemotePath(name!))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    void ToggleAllFiles()
    {
        if (busy || fileGrid.Rows.Count == 0) return;
        var checkAll = fileCheckHeader?.HeaderCheckState != CheckState.Checked;
        renderingFileMatrix = true;
        foreach (DataGridViewRow row in fileGrid.Rows)
        {
            row.Cells["FileChecked"].Value = checkAll;
            var name = row.Cells["FileName"].Value?.ToString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (checkAll) checkedFiles.Add(name); else checkedFiles.Remove(name);
        }
        renderingFileMatrix = false;
        UpdateFileCheckHeader();
        UpdateFileActionButtons();
        fileGrid.Refresh();
        summary.Text = checkAll ? $"All {fileGrid.Rows.Count} file(s) checked" : "All file checks cleared; Sync applies to all files";
    }
    void UpdateFileCheckHeader()
    {
        if (fileCheckHeader is null) return;
        var total = fileGrid.Rows.Count;
        var selected = fileGrid.Rows.Cast<DataGridViewRow>().Count(row => row.Cells["FileChecked"].Value is true);
        fileCheckHeader.SetState(selected == 0 ? CheckState.Unchecked : selected == total ? CheckState.Checked : CheckState.Indeterminate);
    }
    void UpdateFileActionButtons()
    {
        deleteFilesButton.Enabled = !busy && (checkedFiles.Count > 0 || fileGrid.SelectedRows.Count > 0);
    }
    void SelectAll(bool selected) { foreach (var d in devices) d.Selected = selected; deviceGrid.Refresh(); RenderFileMatrix(); }
    void WriteLog(string message) { if (InvokeRequired) { BeginInvoke(() => WriteLog(message)); return; } var line = $"{DateTime.Now:O}  {message}{Environment.NewLine}"; log.AppendText(line); try { Directory.CreateDirectory(Path.GetDirectoryName(sessionLogPath)!); File.AppendAllText(sessionLogPath,line); } catch { } }
    void LogTransferFailure(Device device, string path, Exception error) => WriteLog($"TRANSFER FAILURE drive={device.Name} address={device.BaseUri} firmware={device.Firmware} file={path} elapsed={transferClock.Elapsed.TotalSeconds:0.0}s exception={error.GetType().Name} detail={error.Message} inner={error.InnerException?.Message}. Log: {sessionLogPath}");
    void SetBusy(bool value)
    {
        busy = value;
        addButton.Enabled = syncButton.Enabled = refreshFilesButton.Enabled = deleteFilesButton.Enabled = updateNowButton.Enabled = !value;
        UseWaitCursor = value;
        Cursor = value ? Cursors.WaitCursor : Cursors.Default;
        deviceGrid.UseWaitCursor = value;
        fileGrid.UseWaitCursor = value;
        if (!value)
        {
            deviceGrid.Cursor = Cursors.Default;
            fileGrid.Cursor = Cursors.Default;
            UpdateFileActionButtons();
        }
    }
    void SetStatus(Device device, string status) { if (InvokeRequired) { BeginInvoke(() => SetStatus(device, status)); return; } device.Status = status; deviceGrid.Refresh(); summary.Text = $"{device.Name}: {status}"; }
    static string FormatSize(long value) => value >= 1L << 30 ? $"{value / (double)(1L << 30):0.0} GB" : value >= 1L << 20 ? $"{value / (double)(1L << 20):0.0} MB" : value >= 1L << 10 ? $"{value / 1024d:0.0} KB" : $"{value} B";

    void BeginTransferProgress(long totalBytes, int totalOperations, string text)
    {
        transferCancellation?.Dispose(); transferCancellation = new(); transferWasCancelled = false; cancelTransfer.Visible = cancelTransfer.Enabled = true;
        transferClock.Restart();
        lock (transferProgressGate) { activeTransferBytes.Clear(); transferCompletedBytes = 0; transferTotalBytes = Math.Max(1, totalBytes); transferCompletedOperations = 0; transferTotalOperations = Math.Max(1, totalOperations); }
        transferDetail.Text = text; transferDetail.Visible = transferProgress.Visible = true; transferProgress.Value = 0;
        UseWaitCursor = deviceGrid.UseWaitCursor = fileGrid.UseWaitCursor = false; Cursor = deviceGrid.Cursor = fileGrid.Cursor = Cursors.Default;
    }

    void ReportTransferProgress(string operationId, long bytes, string detail)
    {
        if (InvokeRequired) { BeginInvoke(() => ReportTransferProgress(operationId, bytes, detail)); return; }
        long current; lock (transferProgressGate) { activeTransferBytes[operationId] = Math.Max(0, bytes); current = transferCompletedBytes + activeTransferBytes.Values.Sum(); }
        var percent = Math.Clamp((int)Math.Round(current * 100d / transferTotalBytes), 0, 100);
        transferProgress.Value = Math.Clamp(percent * 10, transferProgress.Minimum, transferProgress.Maximum);
        transferDetail.Text = $"{Math.Min(transferCompletedOperations + 1, transferTotalOperations)}/{transferTotalOperations}  {detail}  {percent}%";
    }

    void CompleteTransferProgress(string operationId, long expectedBytes, string detail)
    {
        if (InvokeRequired) { BeginInvoke(() => CompleteTransferProgress(operationId, expectedBytes, detail)); return; }
        long current; lock (transferProgressGate) { var observed = activeTransferBytes.GetValueOrDefault(operationId); activeTransferBytes.Remove(operationId); transferCompletedBytes += Math.Clamp(observed, 0, Math.Max(0, expectedBytes)); transferCompletedOperations++; current = transferCompletedBytes + activeTransferBytes.Values.Sum(); }
        var percent = Math.Clamp((int)Math.Round(current * 100d / transferTotalBytes), 0, 100);
        transferProgress.Value = Math.Clamp(percent * 10, transferProgress.Minimum, transferProgress.Maximum);
        transferDetail.Text = $"{Math.Min(transferCompletedOperations, transferTotalOperations)}/{transferTotalOperations}  {detail}  {percent}%";
    }

    void EndTransferProgress()
    {
        if (InvokeRequired) { BeginInvoke(EndTransferProgress); return; }
        transferProgress.Visible = transferDetail.Visible = false; transferProgress.Value = 0;
        cancelTransfer.Visible = false;
        lock (transferProgressGate) activeTransferBytes.Clear();
    }

    void RecordUploadedFile(Device device, string name, long size)
    {
        if (!inventories.TryGetValue(device.Id, out var files)) inventories[device.Id] = files = [];
        var remotePath = FlyingThumbClient.NormalizeRemotePath(name);
        var existing = files.FirstOrDefault(f => f.Type == "file" && string.Equals(FlyingThumbClient.NormalizeRemotePath(f.Name), remotePath, StringComparison.OrdinalIgnoreCase));
        if (existing is null) files.Add(new RemoteFile { Name = "/" + remotePath, Size = size, Type = "file" });
        else { existing.Name = "/" + remotePath; existing.Size = size; }
    }

    void RecordDeletedFile(Device device, string name)
    {
        if (!inventories.TryGetValue(device.Id, out var files)) return;
        var remotePath = FlyingThumbClient.NormalizeRemotePath(name);
        files.RemoveAll(f => f.Type == "file" && string.Equals(FlyingThumbClient.NormalizeRemotePath(f.Name), remotePath, StringComparison.OrdinalIgnoreCase));
    }
    async Task RefreshDevices()
    {
        if (busy) return; SetBusy(true); summary.Text = "Searching the local network..."; WriteLog("Searching for Flying Thumb drives...");
        try { var found = await DeviceDiscovery.FindAsync(TimeSpan.FromSeconds(4)); ReplaceDevices(found); summary.Text = $"Found {found.Count} drive{(found.Count == 1 ? "" : "s")}"; WriteLog(summary.Text + "."); }
        catch (Exception ex) { summary.Text = "Discovery failed"; WriteLog("Discovery failed: " + ex.Message); }
        finally { SetBusy(false); }
        await RefreshFileMatrix();
        await CheckForUpdates(false);
    }

    void ReplaceDevices(IEnumerable<Device> found)
    {
        var selected = devices.ToDictionary(d => d.Id, d => d.Selected, StringComparer.OrdinalIgnoreCase);
        devices.Clear();
        foreach (var d in found)
        {
            if (selected.TryGetValue(d.Id, out var wasSelected)) d.Selected = wasSelected;
            devices.Add(d);
        }
    }

    async Task WaitForReconnectAndRefresh(Device[] targets, string operation, bool rereadFiles = false)
    {
        var realTargets = targets.Where(d => !d.IsSimulated).ToArray();
        if (realTargets.Length == 0)
        {
            SetBusy(false);
            RenderFileMatrix();
            return;
        }

        var wanted = realTargets.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var d in realTargets) SetStatus(d, "Waiting to reconnect...");
        summary.Text = $"{operation} complete; waiting for drives to reconnect...";
        await Task.Delay(1500);

        for (var attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                var found = await DeviceDiscovery.FindAsync(TimeSpan.FromSeconds(2));
                if (wanted.All(id => found.Any(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))))
                {
                    ReplaceDevices(found);
                    foreach (var d in devices.Where(d => wanted.Contains(d.Id)))
                        WriteLog($"{d.Name}: reconnected on firmware {d.Firmware}.");
                    SetBusy(false);
                    if (rereadFiles) await RefreshFileMatrix();
                    else
                    {
                        foreach (var d in devices.Where(d => wanted.Contains(d.Id))) SetStatus(d, "Ready");
                        RenderFileMatrix();
                    }
                    return;
                }
            }
            catch (Exception ex) { WriteLog($"Reconnect check: {ex.Message}"); }
            await Task.Delay(1000);
        }

        SetBusy(false);
        WriteLog($"{operation} finished, but one or more drives did not reconnect within 45 seconds.");
        summary.Text = $"{operation} finished; reconnect still pending";
        await RefreshDevices();
    }

    async Task RefreshFileMatrix()
    {
        if (busy || devices.Count == 0) return;
        SetBusy(true);
        summary.Text = "Reading files from all drives...";
        inventories.Clear();
        await Task.WhenAll(devices.Select(async d =>
        {
            try { inventories[d.Id] = await client.ListAsync(d, Key); SetStatus(d, ReadyStatus(d)); }
            catch (Exception ex) { inventories[d.Id] = []; SetStatus(d, "Unavailable"); WriteLog($"{d.Name}: could not read files - {ex.Message}"); }
        }));
        SetBusy(false);
        RenderFileMatrix();
    }

    void RenderFileMatrix()
    {
        var shown = devices.Where(d => d.Selected).ToArray();
        var names = shown.Where(d => inventories.ContainsKey(d.Id)).SelectMany(d => inventories[d.Id]).Where(x => x.Type == "file").Select(x => FlyingThumbClient.NormalizeRemotePath(x.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        checkedFiles.IntersectWith(names);
        renderingFileMatrix = true;
        fileGrid.SuspendLayout();
        fileGrid.Columns.Clear();
        fileGrid.Rows.Clear();
        fileCheckHeader = new CheckBoxHeaderCell { ToolTipText = "Check or uncheck all files" };
        fileCheckHeader.ToggleRequested += (_, _) => ToggleAllFiles();
        fileGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "FileChecked", HeaderText = "", HeaderCell = fileCheckHeader, ReadOnly = false, Width = 46, MinimumWidth = 46, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, SortMode = DataGridViewColumnSortMode.NotSortable });
        fileGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FileName", HeaderText = "File", ReadOnly = true, FillWeight = 150 });
        fileGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Overall", HeaderText = "Overall", ReadOnly = true, FillWeight = 65 });
        foreach (var d in shown) fileGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = d.Name, ReadOnly = true, FillWeight = 75 });
        foreach (var name in names)
        {
            var entries = shown.Select(d => inventories.TryGetValue(d.Id, out var files) ? files.FirstOrDefault(f => string.Equals(FlyingThumbClient.NormalizeRemotePath(f.Name), name, StringComparison.OrdinalIgnoreCase)) : null).ToArray();
            var sizes = entries.Where(x => x is not null).Select(x => x!.Size).Distinct().ToArray();
            var state = sizes.Length > 1 ? "CONFLICT" : entries.All(x => x is not null) ? "On every included drive" : $"On {entries.Count(x => x is not null)}/{shown.Length}";
            var cells = new List<object> { checkedFiles.Contains(name), name, state };
            cells.AddRange(entries.Select(x => x is null ? "-" : $"Yes - {FormatSize(x.Size)}"));
            var row = fileGrid.Rows[fileGrid.Rows.Add(cells.ToArray())];
            if (sizes.Length > 1) row.DefaultCellStyle.BackColor = Color.MistyRose;
        }
        fileGrid.ResumeLayout();
        renderingFileMatrix = false;
        UpdateFileCheckHeader();
        UpdateFileActionButtons();
        tabs.SelectedIndex = 0;
        summary.Text = shown.Length == 0 ? "No drives included" : $"Showing {fileGrid.Rows.Count} unique file(s) across {shown.Length} included drive(s)";
    }

    async Task ChooseAndSync()
    {
        if (busy) return;
        var targets = SelectedDevices();
        if (targets.Length < 2) { MessageBox.Show(this, "Include at least two drives before syncing.", "Sync Drives", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        var checkedNames = CheckedFileNames();
        if (checkedNames.Length > 0) await SyncAcrossDevices(checkedNames); else await SyncAcrossDevices();
    }

    async Task SyncSelectedFiles()
    {
        var names = ChosenFileNames();
        if (names.Length > 0) await SyncAcrossDevices(names);
    }
    async Task DeleteSelectedFiles()
    {
        if (busy) return;
        var names = ChosenFileNames();
        if (names.Length == 0) return;

        var included = SelectedDevices();
        var work = included.Select(device => (Device: device, Names: names.Where(name => inventories.TryGetValue(device.Id, out var files) && files.Any(file => file.Type == "file" && string.Equals(FlyingThumbClient.NormalizeRemotePath(file.Name), name, StringComparison.OrdinalIgnoreCase))).ToArray()))
            .Where(item => item.Names.Length > 0)
            .ToArray();
        var targets = work.Select(item => item.Device).ToArray();
        if (targets.Length == 0) { MessageBox.Show(this, "The selected files are not on any included drive.", "Delete Files", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (!EnsureStorageAvailable(targets) || !EnsureManagementKey(targets) || !EnsureManagedFirmware(targets)) return;

        var copies = work.Sum(item => item.Names.Length);
        var copyWord = copies == 1 ? "copy" : "copies";
        var preview = string.Join("\n", names.Take(8).Select(name => "  " + name));
        if (names.Length > 8) preview += $"\n  ...and {names.Length - 8} more";
        var switching = targets.Count(device => !device.IsSimulated && !device.UsbManaged);
        var modeNote = switching > 0 ? $"\n\nUSB access on {switching} drive{(switching == 1 ? "" : "s")} will pause during deletion, then reconnect as writable automatically." : "";
        if (MessageBox.Show(this, $"Permanently delete {names.Length} selected file{(names.Length == 1 ? "" : "s")} ({copies} total {copyWord}) from the included drives?\n\n{preview}{modeNote}\n\nThis cannot be undone.", "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        SetBusy(true);
        tabs.SelectedIndex = 1;
        WriteLog($"Deleting {copies} file {copyWord} across {targets.Length} included drive(s)...");
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batchMode = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var device in targets)
        {
            try { batchMode[device.Id] = await client.BeginFileBatchAsync(device, Key); if (batchMode[device.Id] && UsesManagedUsb(device)) device.UsbManaged = true; }
            catch (Exception ex) { blocked.Add(device.Id); failed.Add(device.Id); errors.Add($"{device.Name} / prepare delete: {ex.Message}"); SetStatus(device, "Could not prepare delete"); }
        }

        var deleted = 0;
        foreach (var item in work.Where(item => !blocked.Contains(item.Device.Id)))
        {
            foreach (var name in item.Names)
            {
                SetStatus(item.Device, $"Deleting {name}...");
                try { await client.DeleteAsync(item.Device, name, Key); RecordDeletedFile(item.Device, name); deleted++; WriteLog($"{item.Device.Name}: deleted {name}."); }
                catch (Exception ex) { failed.Add(item.Device.Id); errors.Add($"{item.Device.Name} / {name}: {ex.Message}"); WriteLog($"{item.Device.Name}: FAILED to delete {name} - {ex.Message}"); }
            }
        }

        var sessions = targets.Where(device => !blocked.Contains(device.Id) && batchMode.GetValueOrDefault(device.Id)).ToArray();
        foreach (var device in sessions)
        {
            try { await client.CommitFileBatchAsync(device, Key); device.UsbManaged = false; }
            catch (Exception ex) { failed.Add(device.Id); errors.Add($"{device.Name} / USB refresh: {ex.Message}"); WriteLog($"{device.Name}: USB refresh failed - {ex.Message}"); }
        }
        foreach (var device in devices.Where(device => sessions.Any(session => session.Id == device.Id) && !failed.Contains(device.Id))) SetStatus(device, ReadyStatus(device));
        foreach (var device in devices.Where(device => failed.Contains(device.Id))) SetStatus(device, "Delete incomplete");
        SetBusy(false);
        RenderFileMatrix();
        summary.Text = errors.Count == 0 ? $"Deleted {deleted} file {(deleted == 1 ? "copy" : "copies")}" : $"Delete finished with {errors.Count} error(s)";
        WriteLog($"Delete finished: {deleted} of {copies} file {copyWord} removed, {errors.Count} error(s).");
        if (errors.Count > 0) MessageBox.Show(this, "Some files could not be deleted.\n\n" + string.Join("\n", errors.Take(10)), "Delete Incomplete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
    async Task ChooseAndAddFiles()
    {
        using var picker = new OpenFileDialog { Multiselect = true, Title = "Choose files to add" };
        if (picker.ShowDialog(this) == DialogResult.OK) await AddPaths(picker.FileNames);
    }

    async Task ChooseAndAddFolder()
    {
        using var picker = new FolderBrowserDialog { Description = "Choose a folder to copy, including all of its contents" };
        if (picker.ShowDialog(this) == DialogResult.OK) await AddPaths([picker.SelectedPath]);
    }

    static UploadItem[] ExpandUploadItems(IEnumerable<string> paths)
    {
        var items = new List<UploadItem>();
        foreach (var path in paths)
        {
            if (File.Exists(path)) items.Add(new UploadItem(path, Path.GetFileName(path)));
            else if (Directory.Exists(path))
            {
                var rootName = new DirectoryInfo(path).Name;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    items.Add(new UploadItem(file, rootName + "/" + Path.GetRelativePath(path, file).Replace('\\', '/')));
            }
        }
        return items.GroupBy(item => item.RemotePath, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
    }

    async Task AddPaths(string[] paths)
    {
        var items = ExpandUploadItems(paths);
        if (items.Length == 0) { MessageBox.Show(this, "The selected folder contains no files.", "Add Folder", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        await AddFiles(items);
    }

    async Task AddFiles(UploadItem[] items)
    {
        if (busy || items.Length == 0) return;
        var targets = SelectedDevices();
        if (targets.Length == 0) { MessageBox.Show("Select at least one drive first."); return; }
        if (!EnsureStorageAvailable(targets) || !EnsureManagementKey(targets)) return;
        if (!EnsureManagedFirmware(targets)) return;
        if (items.Any(item => item.RemotePath.Contains('/')) && !EnsureFolderFirmware(targets)) return;
        if (!ConfirmManagedUsb(targets)) return;

        SetBusy(true);
        tabs.SelectedIndex = 1;
        WriteLog($"Adding {items.Length} file(s) to {targets.Length} selected drive(s) as one batch...");
        var fileSizes = items.ToDictionary(item => item.LocalPath, item => new FileInfo(item.LocalPath).Length, StringComparer.OrdinalIgnoreCase);
        BeginTransferProgress(fileSizes.Values.Sum() * targets.Length, items.Length * targets.Length, "Preparing transfer...");
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batchMode = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var gate = new object();

        foreach (var device in targets)
        {
            try { batchMode[device.Id] = await client.BeginFileBatchAsync(device, Key); if (batchMode[device.Id] && UsesManagedUsb(device)) device.UsbManaged = true; }
            catch (Exception ex)
            {
                blocked.Add(device.Id); failed.Add(device.Id);
                errors.Add($"{device.Name} / prepare transfer: {ex.Message}");
                SetStatus(device, "Could not prepare transfer");
            }
        }

        await Task.WhenAll(targets.Where(d => !blocked.Contains(d.Id)).Select(async device =>
        {
            foreach (var item in items)
            {
                if (transferCancellation!.IsCancellationRequested) break;
                var path = item.LocalPath;
                var name = item.RemotePath;
                var operationId = $"{device.Id}:{name}";
                var expectedBytes = fileSizes[path];
                SetStatus(device, $"Adding {name}...");
                WriteLog($"UPLOAD START drive={device.Name} address={device.BaseUri} firmware={device.Firmware} file={name} bytes={expectedBytes}");
                try
                {
                    await client.UploadAsync(device, path, name, Key, bytes => ReportTransferProgress(operationId, bytes, $"{name} to {device.Name}"), transferCancellation!.Token);
                    RecordUploadedFile(device, name, expectedBytes);
                    lock (gate) changed.Add(device.Id);
                    WriteLog($"{device.Name}: added {name}.");
                }
                catch (Exception ex)
                {
                    LogTransferFailure(device, name, ex);
                    if (transferCancellation!.IsCancellationRequested) { SetStatus(device, "Transfer cancelled"); break; }
                    lock (gate) { failed.Add(device.Id); errors.Add($"{device.Name} / {name}: {ex.Message}"); }
                    SetStatus(device, "File failed; continuing batch...");
                    WriteLog($"{device.Name}: FAILED to add {name} - {ex.Message}");
                }
                finally { CompleteTransferProgress(operationId, expectedBytes, $"{name} to {device.Name}"); }
            }
        }));

        var modernSessions = targets.Where(d => !blocked.Contains(d.Id) && batchMode.GetValueOrDefault(d.Id)).ToArray();
        foreach (var device in modernSessions.Where(d => !d.IsSimulated)) SetStatus(device, "Refreshing USB drive...");
        foreach (var device in modernSessions)
        {
            try { await client.CommitFileBatchAsync(device, Key); device.UsbManaged = false; }
            catch (Exception ex)
            {
                failed.Add(device.Id); errors.Add($"{device.Name} / USB refresh: {ex.Message}");
                WriteLog($"{device.Name}: USB refresh failed - {ex.Message}");
            }
        }

        var legacyChanged = targets.Where(d => changed.Contains(d.Id) && !batchMode.GetValueOrDefault(d.Id)).ToArray();
        foreach (var device in legacyChanged.Where(d => !d.IsSimulated)) SetStatus(device, "Updating older drive software view...");
        foreach (var device in legacyChanged)
        {
            try { await client.RestartAsync(device, Key); }
            catch (Exception ex)
            {
                failed.Add(device.Id); errors.Add($"{device.Name} / reconnect: {ex.Message}");
                WriteLog($"{device.Name}: reconnect failed - {ex.Message}");
            }
        }

        EndTransferProgress();
        SetBusy(false);
        if (legacyChanged.Length > 0)
        {
            SetBusy(true);
            await WaitForReconnectAndRefresh(legacyChanged, "File transfer");
        }
        foreach (var device in devices.Where(d => modernSessions.Any(m => m.Id == d.Id) && !failed.Contains(d.Id))) SetStatus(device, ReadyStatus(device));
        foreach (var device in devices.Where(d => failed.Contains(d.Id))) SetStatus(device, "Transfer incomplete");
        RenderFileMatrix();

        if (transferWasCancelled) { summary.Text = "Transfer cancelled; confirmed files retained"; WriteLog("Transfer cancelled; batch cleanup finished."); }
        else if (errors.Count == 0)
        {
            summary.Text = $"Added {items.Length} file(s) to {targets.Length} drive(s)";
            WriteLog("File batch finished successfully; USB refreshed once after all uploads.");
        }
        else
        {
            summary.Text = $"File batch finished with {errors.Count} error(s)";
            MessageBox.Show(this, "The batch continued after individual failures.\n\n" + string.Join("\n", errors.Take(10)) + (errors.Count > 10 ? $"\n\n...and {errors.Count - 10} more." : ""), "Flying Thumb File Transfer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
    async Task SyncAcrossDevices(IReadOnlyCollection<string>? onlyFiles = null)
    {
        if (busy) return;
        var targets = SelectedDevices();
        if (targets.Length < 2) { MessageBox.Show(this, "Include at least two drives before syncing.", "Sync Drives", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (!EnsureStorageAvailable(targets) || !EnsureManagementKey(targets) || !EnsureManagedFirmware(targets)) return;

        SetBusy(true);
        tabs.SelectedIndex = 1;
        var selectedNames = onlyFiles?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        WriteLog(selectedNames is null ? $"Building an additive sync plan across {targets.Length} drives..." : $"Building a sync plan for {selectedNames.Count} selected file(s) across {targets.Length} drives...");
        var current = new Dictionary<string, List<RemoteFile>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var device in targets)
                current[device.Id] = inventories.TryGetValue(device.Id, out var cached) ? cached.Select(file => new RemoteFile { Name = file.Name, Size = file.Size, Type = file.Type }).ToList() : await client.ListAsync(device, Key);
        }
        catch (Exception ex)
        {
            WriteLog("Sync could not start - " + ex.Message); summary.Text = "Sync could not read every selected drive"; SetBusy(false); return;
        }

        var names = current.Values.SelectMany(files => files).Where(file => file.Type == "file").Select(file => FlyingThumbClient.NormalizeRemotePath(file.Name)).Distinct(StringComparer.OrdinalIgnoreCase);
        if (selectedNames is not null) names = names.Where(selectedNames.Contains);
        var namesToSync = names.ToArray();
        if (namesToSync.Length == 0)
        {
            SetBusy(false); summary.Text = selectedNames is null ? "No files found to sync" : "The selected files are no longer in the file view"; WriteLog("No matching files were found; USB mode was not changed."); return;
        }
        if (namesToSync.Any(name => name.Contains('/')) && !EnsureFolderFirmware(targets)) { SetBusy(false); return; }

        var plan = new List<(string Name, (Device Device, RemoteFile File)[] Sources, Device[] Destinations)>();
        string? applyToAllDeviceId = null;
        int conflictsResolved = 0;
        foreach (var name in namesToSync)
        {
            var present = targets.Select(device => (Device: device, File: current[device.Id].FirstOrDefault(file => string.Equals(FlyingThumbClient.NormalizeRemotePath(file.Name), name, StringComparison.OrdinalIgnoreCase))))
                .Where(item => item.File is not null).Select(item => (Device: item.Device, File: item.File!)).ToArray();
            var sizes = present.Select(item => item.File.Size).Distinct().ToArray();
            if (sizes.Length > 1)
            {
                var sourceMatches = applyToAllDeviceId is null ? [] : present.Where(item => item.Device.Id == applyToAllDeviceId).ToArray();
                if (sourceMatches.Length == 0)
                {
                    var choices = present.Select((item, index) => $"{index + 1}. {item.Device.Name} - {FormatSize(item.File.Size)}  ({(item.Device.IsSimulated ? "Demo folder" : item.Device.Ip)})").ToArray();
                    SetBusy(false);
                    var decision = Prompt.ChooseWithApply($"Choose which drive's copy of {name} should win.", "Resolve Sync Conflict", choices, "Apply option to all files", selectedNames is null || selectedNames.Count > 1);
                    if (decision is null) { summary.Text = "Sync canceled - USB mode unchanged"; WriteLog("Sync was canceled while resolving conflicts; USB mode was not changed."); return; }
                    SetBusy(true);
                    var selected = Array.IndexOf(choices, decision.Value.Choice);
                    if (selected < 0) { SetBusy(false); return; }
                    sourceMatches = [present[selected]];
                    if (decision.Value.ApplyToAll) applyToAllDeviceId = present[selected].Device.Id;
                }
                var source = sourceMatches[0];
                var destinations = targets.Where(device => device.Id != source.Device.Id)
                    .Where(device => current[device.Id].FirstOrDefault(file => string.Equals(FlyingThumbClient.NormalizeRemotePath(file.Name), name, StringComparison.OrdinalIgnoreCase))?.Size != source.File.Size).ToArray();
                if (destinations.Length > 0) plan.Add((name, [source], destinations));
                conflictsResolved++;
                WriteLog($"CONFLICT: {name} will use {source.Device.Name}'s version{(applyToAllDeviceId == source.Device.Id ? " (applied to remaining conflicts)" : "")}.");
                continue;
            }
            var missing = targets.Where(device => !present.Any(item => item.Device.Id == device.Id)).ToArray();
            if (missing.Length > 0) plan.Add((name, present, missing));
        }

        var plannedCopies = plan.Sum(item => item.Destinations.Length);
        WriteLog($"Sync plan: {plannedCopies} file copy operation(s), {conflictsResolved} conflict choice(s).");
        if (plannedCopies == 0) { SetBusy(false); summary.Text = "Already synchronized - USB mode unchanged"; WriteLog("No files need copying; USB mode was not changed."); return; }
        SetBusy(false); if (!ConfirmManagedUsb(targets)) return; SetBusy(true);
        var plannedTransferBytes = plan.Sum(item => item.Sources[0].File.Size * (1L + item.Destinations.Length));
        BeginTransferProgress(plannedTransferBytes, plan.Count + plannedCopies, "Preparing sync...");

        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batchMode = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var device in targets)
        {
            try { batchMode[device.Id] = await client.BeginFileBatchAsync(device, Key); if (batchMode[device.Id] && UsesManagedUsb(device)) device.UsbManaged = true; }
            catch (Exception ex) { blocked.Add(device.Id); failed.Add(device.Id); errors.Add($"{device.Name} / prepare sync: {ex.Message}"); SetStatus(device, "Could not prepare sync"); }
        }

        int copied = 0;
        var temp = Path.Combine(Path.GetTempPath(), "FlyingThumb", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        try
        {
            foreach (var item in plan)
            {
                if (transferCancellation!.IsCancellationRequested) break;
                var availableSources = item.Sources.Where(source => !blocked.Contains(source.Device.Id)).ToArray();
                if (availableSources.Length == 0) { errors.Add($"Could not read {item.Name}: its chosen source drive did not enter managed mode."); WriteLog($"FAILED to stage {item.Name}; no prepared source drive is available."); continue; }
                var source = availableSources[0];
                var destinations = item.Destinations.Where(device => !blocked.Contains(device.Id)).ToArray();
                if (destinations.Length == 0) continue;
                var local = Path.Combine(temp, Guid.NewGuid().ToString("N") + Path.GetExtension(item.Name));
                var downloadId = $"download:{source.Device.Id}:{item.Name}";
                try { await client.DownloadAsync(source.Device, item.Name, local, bytes => ReportTransferProgress(downloadId, bytes, $"Reading {item.Name} from {source.Device.Name}"), transferCancellation!.Token); }
                catch (Exception ex) { LogTransferFailure(source.Device,item.Name,ex); if(transferCancellation!.IsCancellationRequested) break; errors.Add($"Could not read {item.Name} from {source.Device.Name}: {ex.Message}"); WriteLog($"FAILED to stage {item.Name}; continuing sync - {ex.Message}"); continue; }
                finally { CompleteTransferProgress(downloadId, source.File.Size, $"Read {item.Name}"); }
                foreach (var destination in destinations)
                {
                    if (transferCancellation!.IsCancellationRequested) break;
                    var uploadId = $"upload:{destination.Id}:{item.Name}";
                    SetStatus(destination, $"Syncing {item.Name}...");
                    try { await client.UploadAsync(destination, local, item.Name, Key, bytes => ReportTransferProgress(uploadId, bytes, $"{item.Name} to {destination.Name}"), transferCancellation!.Token); RecordUploadedFile(destination, item.Name, source.File.Size); copied++; SetStatus(destination, "Synced"); WriteLog($"{destination.Name}: copied {item.Name} from {source.Device.Name}."); }
                    catch (Exception ex) { LogTransferFailure(destination,item.Name,ex); if(transferCancellation!.IsCancellationRequested) break; failed.Add(destination.Id); errors.Add($"{destination.Name} / {item.Name}: {ex.Message}"); SetStatus(destination, "File failed; continuing sync..."); WriteLog($"{destination.Name}: FAILED to copy {item.Name}; continuing - {ex.Message}"); }
                    finally { CompleteTransferProgress(uploadId, source.File.Size, $"{item.Name} to {destination.Name}"); }
                }
            }

            var sessions = targets.Where(device => !blocked.Contains(device.Id) && batchMode.GetValueOrDefault(device.Id)).ToArray();
            foreach (var device in sessions.Where(device => !device.IsSimulated)) SetStatus(device, "Refreshing USB drive...");
            foreach (var device in sessions)
                try { await client.CommitFileBatchAsync(device, Key); device.UsbManaged = false; } catch (Exception ex) { failed.Add(device.Id); errors.Add($"{device.Name} / USB refresh: {ex.Message}"); WriteLog($"{device.Name}: USB refresh failed - {ex.Message}"); }

            WriteLog($"Sync finished: {copied} of {plannedCopies} planned copy operation(s), {conflictsResolved} conflict choice(s), {errors.Count} error(s).");
            foreach (var device in devices.Where(device => sessions.Any(session => session.Id == device.Id) && !failed.Contains(device.Id))) SetStatus(device, ReadyStatus(device));
            foreach (var device in devices.Where(device => failed.Contains(device.Id))) SetStatus(device, "Sync incomplete");
            EndTransferProgress(); SetBusy(false); RenderFileMatrix(); summary.Text = errors.Count == 0 ? $"Sync complete - {copied} copies" : $"Sync finished with {errors.Count} error(s)";
            if (transferWasCancelled) summary.Text = $"Sync cancelled; {copied} confirmed copies retained";
            if (errors.Count > 0) MessageBox.Show(this, "Sync continued after individual failures.\n\n" + string.Join("\n", errors.Take(10)) + (errors.Count > 10 ? $"\n\n...and {errors.Count - 10} more." : ""), "Flying Thumb Sync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { try { Directory.Delete(temp, true); } catch { } EndTransferProgress(); SetBusy(false); }
    }
    async Task<bool> RunForDevices(Device[] targets, Func<Device, Task<string>> action)
    {
        if (!EnsureManagementKey(targets)) return false;
        var failed = 0;
        SetBusy(true); tabs.SelectedIndex = 1;
        await Task.WhenAll(targets.Select(async d => { try { var result = await action(d); SetStatus(d, result); WriteLog($"{d.Name}: {result}."); } catch (Exception ex) { Interlocked.Exchange(ref failed, 1); SetStatus(d, "Failed"); WriteLog($"{d.Name}: FAILED - {ex.Message}"); } }));
        summary.Text = "Operation finished"; SetBusy(false);
        return failed == 0;
    }

    static UpdateAsset FirmwareFor(UpdateManifest manifest, Device device) => manifest.Firmware;
    static UpdateAsset RecoveryFor(UpdateManifest manifest, bool screenless) => manifest.Recovery;
    Device[] DrivesNeedingUpdate(UpdateManifest manifest) => devices.Where(d =>
    {
        if (d.IsSimulated) return false;
        var asset = FirmwareFor(manifest, d);
        return !string.IsNullOrWhiteSpace(asset.Version) && UpdateService.IsNewer(asset.Version, d.Firmware);
    }).ToArray();

    async Task CheckForUpdates(bool showResult)
    {
        if (busy) return;
        SetBusy(true);
        if (showResult) summary.Text = "Checking for updates...";
        try
        {
            var manifest = await UpdateService.GetLatestAsync();
            var outdatedDrives = DrivesNeedingUpdate(manifest);
            var managerNeedsUpdate = UpdateService.IsNewer(manifest.Manager.Version, UpdateService.CurrentManagerVersion);
            availableUpdate = outdatedDrives.Length > 0 || managerNeedsUpdate ? manifest : null;
            updateBanner.Visible = availableUpdate is not null;
            if (availableUpdate is not null)
            {
                var parts = new List<string>();
                if (outdatedDrives.Length > 0) parts.Add($"{outdatedDrives.Length} drive{(outdatedDrives.Length == 1 ? "" : "s")}");
                if (managerNeedsUpdate) parts.Add("Flying Thumb Manager");
                updateNotice.Text = "Update available for " + string.Join(" and ", parts) + ".";
                WriteLog(updateNotice.Text);
                summary.Text = "Update available";
            }
            else
            {
                updateNotice.Text = "";
                if (showResult) MessageBox.Show(this, "Flying Thumb Manager and all discovered drives are up to date.", "Flying Thumb Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            WriteLog("Update check unavailable - " + ex.Message);
            if (showResult) MessageBox.Show(this, "Updates could not be checked right now.\n\n" + ex.Message, "Flying Thumb Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally { SetBusy(false); }
    }

    async Task InstallAvailableUpdates()
    {
        var manifest = availableUpdate;
        if (manifest is null) { await CheckForUpdates(true); manifest = availableUpdate; if (manifest is null) return; }
        var outdatedDrives = DrivesNeedingUpdate(manifest);
        var managerNeedsUpdate = UpdateService.IsNewer(manifest.Manager.Version, UpdateService.CurrentManagerVersion);
        var description = new List<string>();
        if (outdatedDrives.Length > 0) description.Add($"update {outdatedDrives.Length} drive{(outdatedDrives.Length == 1 ? "" : "s")}");
        if (managerNeedsUpdate) description.Add("update Flying Thumb Manager");
        var updateDetails = outdatedDrives.Length > 0
            ? "\n\nKeep the drives powered until they reconnect."
            : "\n\nThe Manager will close and restart automatically.";
        if (MessageBox.Show(this, "Ready to " + string.Join(" and ", description) + "." + updateDetails, "Install Flying Thumb Updates", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;

        SetBusy(true);
        tabs.SelectedIndex = 1;
        var errors = new List<string>();
        var successfulDrives = new List<Device>();
        string? downloadedManager = null;
        try
        {
            // The banner may have been displayed for a while. Refresh immediately
            // before downloading so the published hashes cannot be stale.
            summary.Text = "Confirming the latest update...";
            manifest = await UpdateService.GetLatestAsync();
            outdatedDrives = DrivesNeedingUpdate(manifest);
            managerNeedsUpdate = UpdateService.IsNewer(manifest.Manager.Version, UpdateService.CurrentManagerVersion);

            if (outdatedDrives.Length == 0 && !managerNeedsUpdate)
            {
                availableUpdate = null;
                updateBanner.Visible = false;
                summary.Text = "Everything is up to date";
                MessageBox.Show(this, "Flying Thumb Manager and all discovered drives are up to date.", "Flying Thumb Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (outdatedDrives.Length > 0)
            {
                summary.Text = "Downloading drive update...";
                var firmwarePaths = new Dictionary<bool, string>();
                foreach (var screenless in new[] { false })
                {
                    var firmwareAsset = screenless ? manifest.FirmwareScreenless : manifest.Firmware;
                    var firmwareName = screenless ? "FlyingThumb-v2-screenless-wifi-update.bin" : "FlyingThumb-v2-wifi-update.bin";
                    var firmwarePath = await UpdateService.DownloadVerifiedAsync(firmwareAsset, firmwareName);
                    firmwarePaths[screenless] = firmwarePath;
                    try { File.Copy(firmwarePath, Path.Combine(AppContext.BaseDirectory, firmwareName), true); } catch (Exception ex) { WriteLog("Could not cache the Wi-Fi update locally - " + ex.Message); }

                    var recoveryAsset = RecoveryFor(manifest, screenless);
                    var recoveryName = screenless ? "FlyingThumb-v2-screenless-full.bin" : "FlyingThumb-v2-full.bin";
                    if (string.IsNullOrWhiteSpace(recoveryAsset.Url)) continue;
                    try
                    {
                        var recoveryPath = await UpdateService.DownloadVerifiedAsync(recoveryAsset, recoveryName);
                        File.Copy(recoveryPath, Path.Combine(AppContext.BaseDirectory, recoveryName), true);
                    }
                    catch (Exception ex) { WriteLog("Could not refresh the USB recovery image - " + ex.Message); }
                }

                await Task.WhenAll(outdatedDrives.Select(async device =>
                {
                    try
                    {
                        SetStatus(device, "Installing update...");
                        await client.UpgradeFirmwareAsync(device, firmwarePaths[false], Key);
                        lock (successfulDrives) successfulDrives.Add(device);
                        WriteLog($"{device.Name}: update installed; reconnecting.");
                    }
                    catch (Exception ex)
                    {
                        lock (errors) errors.Add($"{device.Name}: {ex.Message}");
                        SetStatus(device, "Update failed");
                        WriteLog($"{device.Name}: update FAILED - {ex.Message}");
                    }
                }));

                if (successfulDrives.Count > 0)
                {
                    SetBusy(true);
                    await WaitForReconnectAndRefresh(successfulDrives.ToArray(), "Drive update", rereadFiles: true);
                }
            }

            if (managerNeedsUpdate)
            {
                SetBusy(true);
                summary.Text = "Downloading Manager update...";
                downloadedManager = await UpdateService.DownloadVerifiedAsync(manifest.Manager, "FlyingThumbManager.exe");
            }

            if (errors.Count > 0)
                MessageBox.Show(this, "Some drive updates did not finish.\n\n" + string.Join("\n", errors), "Flying Thumb Updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            if (managerNeedsUpdate && downloadedManager is not null)
            {
                WriteLog("Manager update downloaded and verified; restarting Manager.");
                UpdateService.LaunchSelfUpdate(downloadedManager);
                Close();
                return;
            }

            availableUpdate = null;
            updateBanner.Visible = false;
            await CheckForUpdates(false);
        }
        catch (Exception ex)
        {
            WriteLog("Update failed - " + ex.Message);
            MessageBox.Show(this, "The update could not be completed.\n\n" + ex.Message, "Flying Thumb Updates", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { SetBusy(false); }
    }

    async void UpgradeFirmware(object? sender, EventArgs e)
    {
        var targets = SelectedDevices(); if (targets.Length == 0) { MessageBox.Show("Select at least one drive."); return; }
        var packaged = Path.Combine(AppContext.BaseDirectory, "FlyingThumb-v2-wifi-update.bin");
        if (!File.Exists(packaged)) { MessageBox.Show("The packaged Wi-Fi update is missing. Re-copy the complete manager folder."); return; }
        if (MessageBox.Show($"Install the available software update on {targets.Length} selected drive(s)?\n\nThe drives will reconnect automatically when finished.", "Confirm drive update", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        var succeeded = await RunForDevices(targets, async d => { SetStatus(d, "Installing update..."); await client.UpgradeFirmwareAsync(d, packaged, Key); return "Update installed; reconnecting"; });
        if (succeeded) { SetBusy(true); await WaitForReconnectAndRefresh(targets, "Firmware update"); }
    }

    async void EnterNetworkUsbRecovery(object? sender, EventArgs e)
    {
        if (deviceGrid.CurrentRow?.DataBoundItem is not Device device || device.IsSimulated) { MessageBox.Show(this, "Choose a physical drive first.", "USB Recovery"); return; }
        if (!device.UsbRecoveryAvailable) { MessageBox.Show(this, "Update this drive's firmware to enable remote USB recovery.", "USB Recovery"); return; }
        if (!device.Claimed) { MessageBox.Show(this, "Set a shop management key on the drive first.", "USB Recovery"); return; }
        if (!EnsureManagementKey([device])) return;
        if (MessageBox.Show(this, "Safely eject the drive from its USB host first.\n\nThis stops Wi-Fi and USB storage until the drive is restarted. For installation or diagnostics, its USB plug must be connected to this PC.\n\nRestart into USB recovery?", "USB Recovery", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        await RunForDevices([device], async item => { await client.EnterUsbRecoveryAsync(item, Key); return "USB recovery requested"; });
    }

    async void RenameDevice(object? sender, EventArgs e)
    {
        if (deviceGrid.CurrentRow?.DataBoundItem is not Device d) { MessageBox.Show("Choose one drive first."); return; }
        var name = Prompt.Show("Machine name", "Rename Flying Thumb", d.Name); if (string.IsNullOrWhiteSpace(name)) return;
        await RunForDevices([d], async item => { await client.RenameAsync(item, name.Trim(), Key); return $"Renamed to {name.Trim()}"; });
    }

    static string[] SerialPorts()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
        return key?.GetValueNames().Select(n => key.GetValue(n)?.ToString()).Where(v => !string.IsNullOrWhiteSpace(v)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray() ?? [];
    }

    async Task<string[]> IdentifyEsp32RecoveryPorts(string flasher, IEnumerable<string> candidates)
    {
        var matches = new List<string>();
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var start = new ProcessStartInfo(flasher) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var argument in new[] { "--chip", "esp32s3", "--port", candidate, "--before", "default_reset", "--after", "no_reset", "chip_id" }) start.ArgumentList.Add(argument);
                using var process = new Process { StartInfo = start };
                process.Start();
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                var exitTask = process.WaitForExitAsync();
                if (await Task.WhenAny(exitTask, Task.Delay(4000)) != exitTask)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync();
                    continue;
                }
                var output = (await outputTask) + (await errorTask);
                if (process.ExitCode == 0 && output.Contains("ESP32-S3", StringComparison.OrdinalIgnoreCase)) matches.Add(candidate);
            }
            catch { }
        }
        return matches.ToArray();
    }

    async Task<(int ExitCode, string Output)> RunFlasherDiagnostic(string flasher, string port, params string[] command)
        => await RunFlasherCommand(flasher, port, "default_reset", "no_reset", TimeSpan.FromMinutes(3), command);

    async Task<(int ExitCode, string Output)> RunFlasherCommand(string flasher, string port, string before, string after, TimeSpan timeout, params string[] command)
    {
        if (automatedDiagnosticReport is not null) File.AppendAllText(automatedDiagnosticReport + ".progress", $"{DateTime.UtcNow:O} START {port} {string.Join(' ', command)}{Environment.NewLine}");
        var start = new ProcessStartInfo(flasher) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "--chip", "esp32s3", "--port", port, "--before", before, "--after", after }.Concat(command)) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync();
        if (await Task.WhenAny(exitTask, Task.Delay(timeout)) != exitTask)
        {
            process.Kill(true);
            await process.WaitForExitAsync();
            return (-1, "Diagnostic timed out.");
        }
        var capturedOutput = ((await outputTask) + (await errorTask)).Trim();
        if (automatedDiagnosticReport is not null) File.AppendAllText(automatedDiagnosticReport + ".progress", $"{DateTime.UtcNow:O} EXIT {process.ExitCode} {capturedOutput[^Math.Min(300, capturedOutput.Length)..]}{Environment.NewLine}");
        return (process.ExitCode, capturedOutput);
    }

    string? diagnosticRecoveryPort;
    async Task<bool> WaitForRecoveryPort(string flasher, string port, TimeSpan timeout, string? expectedMac = null)
    {
        diagnosticRecoveryPort = null;
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(1500);
            foreach (var candidate in new[] { port }.Concat(SerialPorts()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
            var probe = await RunFlasherCommand(flasher, candidate, "no_reset", "no_reset", TimeSpan.FromSeconds(15), "chip_id");
            if (probe.ExitCode == 0 && probe.Output.Contains("ESP32-S3", StringComparison.OrdinalIgnoreCase))
            {
                if (expectedMac is not null && DiagnosticReport.ExtractMac(probe.Output) != expectedMac)
                    continue;
                diagnosticRecoveryPort = candidate;
                return true;
            }
            }
        }
        return false;
    }

    async Task StartApplicationWithoutUsbReset(string flasher, string port)
    {
        var identity = await RunFlasherCommand(flasher, port, "no_reset", "no_reset", TimeSpan.FromSeconds(10), "chip_id");
        if (identity.ExitCode != 0) throw new InvalidOperationException("Cannot identify the drive before application startup. " + identity.Output);
        var mac = DiagnosticReport.ExtractMac(identity.Output);
        var reset = await RunFlasherCommand(flasher, port, "no_reset", "no_reset", TimeSpan.FromSeconds(15), "--flyingthumb-boot-reset", "--mac", mac);
        if (reset.ExitCode != 0) throw new InvalidOperationException("The bundled recovery helper could not boot the application. " + reset.Output);
    }

    static string ReadDiagnosticResult(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var length = Array.FindIndex(bytes, value => value is 0 or 0xff);
        if (length < 0) length = bytes.Length;
        var text = System.Text.Encoding.UTF8.GetString(bytes, 0, length).Trim();
        if (!text.StartsWith("FTDIAG|", StringComparison.Ordinal))
            throw new InvalidOperationException("The hardware test returned to recovery mode but did not leave a recognizable result.");
        return text;
    }

    async Task<string?> ChooseRecoveryPort(string flasher, bool requirePositiveIdentification = false)
    {
        var choices = SerialPorts();
        if (choices.Length == 0) return null;
        summary.Text = "Identifying the Flying Thumb recovery port...";
        var identified = await IdentifyEsp32RecoveryPorts(flasher, choices);
        if (identified.Length == 1) return identified[0];
        if (identified.Length == 0 && requirePositiveIdentification) return null;
        var promptChoices = identified.Length > 1 ? identified : choices;
        var prompt = identified.Length > 1
            ? "More than one ESP32-S3 recovery device answered. Choose the Flying Thumb port."
            : "The Manager could not positively identify the recovery device. Unplugging it will reveal which port disappears.";
        return promptChoices.Length == 1 ? promptChoices[0] : Prompt.Choose(prompt, "Choose USB port", promptChoices);
    }

    void ShowDiagnosticReport(string conclusion, string reportText, bool allPassed)
    {
        if (automatedDiagnosticReport is not null)
        {
            File.WriteAllText(automatedDiagnosticReport, conclusion + Environment.NewLine + reportText);
            AutomatedDiagnosticSucceeded = allPassed;
            return;
        }
        using var dialog = new Form
        {
            Text = "Flying Thumb USB Diagnostic Report",
            StartPosition = FormStartPosition.CenterParent,
            MinimumSize = new Size(720, 500),
            Size = new Size(900, 680),
            ShowIcon = false,
            ShowInTaskbar = false,
            Font = Font
        };
        var heading = new Label
        {
            Text = conclusion,
            Dock = DockStyle.Fill,
            AutoSize = true,
            MaximumSize = new Size(820, 0),
            Padding = new Padding(4),
            ForeColor = allPassed ? Color.FromArgb(20, 100, 45) : Color.FromArgb(170, 55, 20)
        };
        var reportBox = new TextBox
        {
            Text = reportText,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9.5f),
            BackColor = Color.White
        };
        var copy = new Button { Text = "Copy", AutoSize = true };
        var save = new Button { Text = "Save As...", AutoSize = true };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };
        copy.Click += (_, _) => { try { Clipboard.SetText(reportText); copy.Text = "Copied"; } catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Could Not Copy", MessageBoxButtons.OK, MessageBoxIcon.Error); } };
        save.Click += (_, _) =>
        {
            using var picker = new SaveFileDialog { Title = "Save Flying Thumb diagnostic report", Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*", FileName = $"FlyingThumb-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
            if (picker.ShowDialog(dialog) != DialogResult.OK) return;
            try { File.WriteAllText(picker.FileName, reportText); }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Could Not Save Report", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange([close, save, copy]);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var reportTabs = new TabControl { Dock = DockStyle.Fill };
        var checksTab = new TabPage("Test results");
        var rawTab = new TabPage("Full report");
        var checks = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells };
        checks.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        checks.DataSource = (reportText.Contains("|SCHEMA|", StringComparison.Ordinal) ? DiagnosticReport.AssessActive(reportText) : DiagnosticReport.Parse(reportText)).ToList();
        checksTab.Controls.Add(checks); rawTab.Controls.Add(reportBox);
        reportTabs.TabPages.Add(checksTab); reportTabs.TabPages.Add(rawTab);
        layout.Controls.Add(heading, 0, 0); layout.Controls.Add(reportTabs, 0, 1); layout.Controls.Add(buttons, 0, 2);
        dialog.Controls.Add(layout); dialog.AcceptButton = close; dialog.CancelButton = close;
        dialog.ShowDialog(this);
    }

    async void DiagnoseNetworkFiles(object? sender, EventArgs e)
    {
        var target = devices.FirstOrDefault(d => d.Selected && !d.IsSimulated);
        if (target is null) { MessageBox.Show(this, "Include a discovered drive first.", "Network Diagnostic"); return; }
        if (!EnsureManagementKey([target])) return;
        SetBusy(true);
        var report = new List<string>();
        var folder = Path.Combine(Path.GetTempPath(), "FlyingThumb", "NetworkDiagnostic", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var name = ".FlyingThumb-diagnostic-" + Guid.NewGuid().ToString("N") + ".bin";
        bool uploaded = false, batch = false;
        try
        {
            summary.Text = "Testing network file access...";
            await client.ListAsync(target, managementKey); report.Add("FTDIAG|0|NETWORK_FILE_LIST|PASS");
            var bytes = new byte[65536]; System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            var source = Path.Combine(folder, "source.bin"); await File.WriteAllBytesAsync(source, bytes);
            batch = await client.BeginFileBatchAsync(target, managementKey);
            uploaded = true;
            await client.UploadAsync(target, source, name, managementKey);
            report.Add("FTDIAG|0|NETWORK_UPLOAD|PASS|BYTES=65536");
            var downloaded = Path.Combine(folder, "readback.bin"); await client.DownloadAsync(target, name, downloaded);
            var actual = await File.ReadAllBytesAsync(downloaded);
            report.Add("FTDIAG|0|NETWORK_READBACK|" + (actual.AsSpan().SequenceEqual(bytes) ? "PASS" : "FAIL") + "|Exact byte comparison");
        }
        catch (Exception ex) { report.Add("FTDIAG|0|NETWORK_FILE_TEST|FAIL|" + ex.Message); }
        finally
        {
            if (uploaded) try { await client.DeleteAsync(target, name, managementKey); report.Add("FTDIAG|0|NETWORK_CLEANUP|PASS"); } catch (Exception ex) { report.Add("FTDIAG|0|NETWORK_CLEANUP|FAIL|" + name + ": " + ex.Message); }
            if (batch) try { await client.CommitFileBatchAsync(target, managementKey); } catch (Exception ex) { report.Add("FTDIAG|0|NETWORK_COMMIT|FAIL|" + ex.Message); }
            SetBusy(false);
            ShowDiagnosticReport("Network file test for " + target.Name, string.Join(Environment.NewLine, report), report.All(line => !line.Contains("|FAIL|")));
            summary.Text = "Network diagnostic finished";
        }
    }

    async void DiagnoseWindowsFiles(object? sender, EventArgs e)
    {
        using var picker = new FolderBrowserDialog { Description = "Select the Flying Thumb USB drive root to test Windows file access", UseDescriptionForTitle = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        var root = Path.GetFullPath(picker.SelectedPath);
        if (root != Path.GetPathRoot(root)) { MessageBox.Show(this, "Select the drive root, such as E:\\.", "Windows File Diagnostic"); return; }
        var testFolder = Path.Combine(root, ".FlyingThumb-diagnostic-" + Guid.NewGuid().ToString("N"));
        var report = new List<string>(); SetBusy(true);
        try
        {
            Directory.CreateDirectory(testFolder);
            var path = Path.Combine(testFolder, "Nested file with spaces.bin");
            var bytes = new byte[65536]; System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            await File.WriteAllBytesAsync(path, bytes);
            report.Add("FTDIAG|0|WINDOWS_WRITE|PASS|BYTES=65536");
            var actual = await File.ReadAllBytesAsync(path);
            report.Add("FTDIAG|0|WINDOWS_READBACK|" + (actual.AsSpan().SequenceEqual(bytes) ? "PASS" : "FAIL"));
            var renamed = Path.Combine(testFolder, "Renamed file.bin"); File.Move(path, renamed);
            report.Add("FTDIAG|0|WINDOWS_RENAME|PASS"); File.Delete(renamed); Directory.Delete(testFolder);
            report.Add("FTDIAG|0|WINDOWS_CLEANUP|PASS");
        }
        catch (Exception ex) { report.Add("FTDIAG|0|WINDOWS_FILE_TEST|FAIL|" + ex.Message + " Temporary folder: " + testFolder); }
        finally { SetBusy(false); ShowDiagnosticReport("Windows file access test on " + root, string.Join(Environment.NewLine, report), report.All(line => !line.Contains("|FAIL|"))); summary.Text = "Windows diagnostic finished"; }
    }

    async void RestoreDiagnostic(object? sender, EventArgs e)
    {
        using var picker = new OpenFileDialog { Title = "Choose the interrupted diagnostic session", Filter = "Diagnostic session|session.json", InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlyingThumb", "DiagnosticBackups") };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        SetBusy(true);
        try
        {
            var session = DiagnosticSession.Load(picker.FileName);
            var folder = Path.GetDirectoryName(picker.FileName)!;
            var flasher = await ResolveDiagnosticHelper();
            var port = await ChooseRecoveryPort(flasher, true) ?? throw new InvalidOperationException("The drive must be available in recovery mode.");
            if (!await WaitForRecoveryPort(flasher, port, TimeSpan.FromSeconds(10), session.Mac)) throw new InvalidOperationException("The original drive is not answering.");
            port = diagnosticRecoveryPort ?? port;
            foreach (var item in new[] { (Offset: "0x10000", Name: "installed-app0.bin"), (Offset: "0xe000", Name: "boot-selection.bin"), (Offset: "0xff0000", Name: "original-coredump.bin") })
            {
                summary.Text = "Restoring original drive: " + item.Name;
                var path = Path.Combine(folder, item.Name);
                var write = await RunFlasherCommand(flasher, port, "no_reset", "no_reset", TimeSpan.FromMinutes(3), "write_flash", item.Offset, path);
                if (write.ExitCode != 0) throw new InvalidOperationException(write.Output);
                var verify = await RunFlasherCommand(flasher, port, "no_reset", "no_reset", TimeSpan.FromMinutes(3), "verify_flash", item.Offset, path);
                if (verify.ExitCode != 0) throw new InvalidOperationException(verify.Output);
            }
            session.Phase = "Restored and verified"; session.Save(folder);
            await StartApplicationWithoutUsbReset(flasher, port);
            summary.Text = "Interrupted diagnostic restored and verified";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Diagnostic Restoration", MessageBoxButtons.OK, MessageBoxIcon.Error); summary.Text = "Diagnostic restoration incomplete"; }
        finally { SetBusy(false); }
    }

    async void DiagnoseUsb(object? sender, EventArgs e)
    {
        string flasher;
        try { flasher = await ResolveDiagnosticHelper(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "USB Diagnostics Unavailable", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        string? port = null;
        try
        {
            SetBusy(true);
            summary.Text = "Finding a Flying Thumb connected over USB...";
            var normal = await RunUsbHelper(flasher, "--flyingthumb-find-usb");
            WriteLog("USB identification: " + normal.Output.Trim());
            var found = normal.Output.Split('\n').Select(line => line.Trim().Split('|')).Where(parts => parts.Length == 4 && parts[0] == "FTUSB" && parts[1] == "FOUND").ToArray();
            if (found.Length > 0)
            {
                var selected = found.Length == 1 ? found[0][2] : Prompt.Choose("Choose the drive to diagnose", "USB Diagnostics", found.Select(parts => parts[2] + " - " + parts[3]).ToArray())?.Split(" - ")[0];
                if (selected is null) return;
                var target = found.Single(parts => parts[2] == selected);
                summary.Text = "Restarting the drive into diagnostic recovery...";
                var recovery = await RunUsbHelper(flasher, "--flyingthumb-usb-recovery", "--port", target[2], "--mac", target[3]);
                if (recovery.ExitCode != 0 && recovery.Output.Contains("Cannot open USB volume", StringComparison.Ordinal))
                {
                    summary.Text = "Windows permission is needed to safely dismount the drive...";
                    var elevated = new ProcessStartInfo(flasher) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                    foreach (var argument in new[] { "--flyingthumb-usb-recovery", "--port", target[2], "--mac", target[3] }) elevated.ArgumentList.Add(argument);
                    using var process = Process.Start(elevated) ?? throw new InvalidOperationException("Windows did not start the USB recovery helper");
                    await process.WaitForExitAsync();
                    recovery = (process.ExitCode, "The drive could not safely enter recovery. Close open files and finish Windows transfers, then retry diagnostics.");
                }
                if (recovery.ExitCode != 0) throw new InvalidOperationException(recovery.Output);
                await Task.Delay(1500);
                if (!await WaitForRecoveryPort(flasher, target[2], TimeSpan.FromSeconds(30), target[3])) throw new InvalidOperationException("The drive acknowledged recovery but did not reappear over USB.");
                port = diagnosticRecoveryPort;
            }
            else port = await ChooseRecoveryPort(flasher, requirePositiveIdentification: true);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "USB Diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        finally { SetBusy(false); }
        if (string.IsNullOrWhiteSpace(port))
        {
            MessageBox.Show(this, "No Flying Thumb answered over USB. Connect it directly to this PC and close other programs using its USB port. Older firmware needs a one-time upgrade to support automatic USB diagnostics. An unresponsive application cannot accept a software recovery command.", "Flying Thumb Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        await RunUsbDiagnosticCore(flasher, port);
    }

    static async Task<(int ExitCode, string Output)> RunUsbHelper(string flasher, params string[] arguments)
    {
        var start = new ProcessStartInfo(flasher) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("USB helper could not start");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); throw; }
        return (process.ExitCode, (await output) + (await error));
    }

    async Task<List<string>> TestDiagnosticNetworkFiles(string mac)
    {
        var results = new List<string>();
        var id = "FT-" + string.Concat(mac.Split(':').Take(3).Reverse()).ToUpperInvariant();
        var discovered = await DeviceDiscovery.FindAsync(TimeSpan.FromSeconds(4));
        var target = discovered.FirstOrDefault(d => !d.IsSimulated && d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (target is null || (target.Claimed && string.IsNullOrEmpty(Key)))
            return ["FTDIAG|0|NETWORK_FILE_TEST|SKIP|Drive not reachable on this PC's network, or management key unavailable"];
        var folder = Path.Combine(Path.GetTempPath(), "FlyingThumb", "NetworkDiagnostic", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var name = ".FlyingThumb-diagnostic-" + Guid.NewGuid().ToString("N") + ".bin";
        bool batch = false, attempted = false;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            batch = await client.BeginFileBatchAsync(target, Key);
            var payload = new byte[65536]; System.Security.Cryptography.RandomNumberGenerator.Fill(payload);
            var source = Path.Combine(folder, "source.bin"); await File.WriteAllBytesAsync(source, payload);
            attempted = true;
            await client.UploadAsync(target, source, name, Key, cancellationToken: cancel.Token);
            var readback = Path.Combine(folder, "readback.bin");
            await client.DownloadAsync(target, name, readback, cancellationToken: cancel.Token);
            results.Add("FTDIAG|0|NETWORK_FILE_TEST|" + ((await File.ReadAllBytesAsync(readback)).SequenceEqual(payload) ? "PASS" : "FAIL") + "|64 KiB upload/readback exact comparison");
        }
        catch (Exception ex) { results.Add("FTDIAG|0|NETWORK_FILE_TEST|FAIL|" + ex.Message); }
        finally
        {
            if (attempted) try { await client.DeleteAsync(target, name, Key); } catch (Exception ex) { results.Add("FTDIAG|0|NETWORK_CLEANUP|FAIL|" + name + ": " + ex.Message); }
            if (batch) try { await client.CommitFileBatchAsync(target, Key); } catch (Exception ex) { results.Add("FTDIAG|0|NETWORK_COMMIT|FAIL|" + ex.Message); }
            try { Directory.Delete(folder, true); } catch { }
        }
        return results;
    }

    public async Task RunUsbDiagnosticWorker(string port, string reportPath, string expectedTargetMac)
    {
        automatedDiagnosticReport = reportPath;
        await RunUsbDiagnosticCore(await ResolveDiagnosticHelper(), port, expectedTargetMac);
    }

    async Task RunUsbDiagnosticCore(string flasher, string port, string? expectedTargetMac = null)
    {
        SetBusy(true); tabs.SelectedIndex = 1; summary.Text = $"Diagnosing Flying Thumb on {port}...";
        var report = new List<string> { $"Flying Thumb USB diagnostic", $"Time: {DateTime.Now:F}", $"Recovery port: {port}", "" };
        var passed = 0;
        var normalApplication = "";
        var diagnosticInstalled = false;
        var bootSelectionBackup = "";
        var backupFolder = "";
        string? diagnosticError = null;
        string? expectedMac = null;
        DiagnosticSession? session = null;
        bool matchedApplication = false;
        bool referenceDiffers = false;
        try
        {
            var connection = await RunFlasherDiagnostic(flasher, port, "chip_id");
            report.Add($"[{(connection.ExitCode == 0 ? "PASS" : "FAIL")}] Processor connection");
            report.Add(connection.Output); report.Add("");
            if (connection.ExitCode != 0)
            {
                report.Add("[NOT RUN] Remaining checks");
                report.Add("The Manager could not establish the initial recovery connection, so later checks would not provide independent results.");
                foreach (var line in report) if (!string.IsNullOrWhiteSpace(line)) WriteLog(line);
                ShowDiagnosticReport("The Manager could not establish a recovery connection. No hardware or firmware conclusion can be drawn from this attempt.", string.Join(Environment.NewLine, report), false);
                summary.Text = "USB diagnostics could not connect";
                return;
            }
            passed++;
            expectedMac = DiagnosticReport.ExtractMac(connection.Output);
            if (expectedTargetMac is not null && expectedMac != expectedTargetMac.ToLowerInvariant())
                throw new InvalidOperationException("Diagnostic target identity differs from the requested drive.");
            var sessionsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlyingThumb", "DiagnosticBackups");
            if (Directory.Exists(sessionsRoot))
            {
                foreach (var sessionFile in Directory.GetFiles(sessionsRoot, "session.json", SearchOption.AllDirectories).OrderDescending())
                {
                    var metadata = System.Text.Json.JsonSerializer.Deserialize<DiagnosticSession>(File.ReadAllText(sessionFile));
                    if (metadata?.Mac != expectedMac || metadata.Phase == "Restored and verified") continue;
                    var security = await RunFlasherDiagnostic(flasher, port, "get_security_info");
                    if (security.ExitCode != 0) throw new InvalidOperationException("Cannot validate security before restoring interrupted diagnostic");
                    DiagnosticReport.ValidateSecurity(security.Output);
                    var interrupted = DiagnosticSession.Load(sessionFile); // Reject altered backups before writing.
                    var folder = Path.GetDirectoryName(sessionFile)!;
                    summary.Text = "Restoring an interrupted diagnostic before starting...";
                    foreach (var item in new[] { (Offset: "0x10000", Name: "installed-app0.bin"), (Offset: "0xe000", Name: "boot-selection.bin"), (Offset: "0xff0000", Name: "original-coredump.bin") })
                    {
                        var path = Path.Combine(folder, item.Name);
                        var write = await RunFlasherCommand(flasher, port, "no_reset", "no_reset", TimeSpan.FromMinutes(3), "write_flash", item.Offset, path);
                        if (write.ExitCode != 0) throw new InvalidOperationException("Interrupted-session restoration failed: " + write.Output);
                        var verify = await RunFlasherCommand(flasher, port, "no_reset", "no_reset", TimeSpan.FromMinutes(3), "verify_flash", item.Offset, path);
                        if (verify.ExitCode != 0) throw new InvalidOperationException("Interrupted-session verification failed: " + verify.Output);
                    }
                    interrupted.Phase = "Restored and verified"; interrupted.Save(folder);
                    report.Add("[PASS] Automatically restored interrupted diagnostic session");
                    break;
                }
            }

            foreach (var test in new[]
            {
                (Name: "Onboard flash", Command: new[] { "flash_id" }),
                (Name: "Security configuration", Command: new[] { "get_security_info" })
            })
            {
                WriteLog($"USB diagnostic: {test.Name}...");
                var result = await RunFlasherDiagnostic(flasher, port, test.Command);
                report.Add($"[{(result.ExitCode == 0 ? "PASS" : "FAIL")}] {test.Name}");
                report.Add(result.Output); report.Add("");
                if (result.ExitCode == 0) passed++;
                if (test.Name == "Security configuration")
                {
                    if (result.ExitCode != 0) throw new InvalidOperationException("The security state could not be read; active diagnostics were not loaded.");
                    DiagnosticReport.ValidateSecurity(result.Output);
                }
                if (test.Name == "Onboard flash")
                {
                    if (result.ExitCode != 0) throw new InvalidOperationException("The flash capacity could not be read; active diagnostics were not loaded.");
                    DiagnosticReport.ValidateFlashCapacity(result.Output);
                }
            }

            (string Path, string Version, string Source) application;
            try { application = await ResolveApplicationImage(screenless: true); }
            catch (Exception ex)
            {
                report.Add("[SKIPPED] Installed firmware comparison");
                report.Add("A verified application image was unavailable: " + ex.Message);
                application = ("", "unknown", "unavailable");
            }
            if (application.Path.Length > 0)
            {
                normalApplication = application.Path;
                WriteLog($"USB diagnostic: comparing immutable application bytes with firmware {application.Version}...");
                var result = await RunFlasherDiagnostic(flasher, port, "verify_flash", "0x10000", application.Path);
                referenceDiffers = result.ExitCode != 0 && result.Output.Contains("digest mismatch", StringComparison.OrdinalIgnoreCase);
                report.Add($"[{(result.ExitCode == 0 ? "PASS" : referenceDiffers ? "DIFFERENT" : "FAIL")}] Installed application comparison with {application.Version}");
                report.Add(result.Output); report.Add("");
                matchedApplication = result.ExitCode == 0;
                if (result.ExitCode == 0 || referenceDiffers) passed++;
                if (referenceDiffers) report.Add("A different firmware version or damaged application can cause this difference; it does not by itself establish a hardware fault. Original bytes will be preserved.");
            }
            if (normalApplication.Length == 0)
                throw new InvalidOperationException("The normal application image is unavailable, so an active test cannot safely replace and restore it.");

            var diagnostic = await ResolveDiagnosticImage();
            if (!System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(diagnostic.Path)).Contains("HWCDC_V1", StringComparison.Ordinal))
                throw new InvalidOperationException("This diagnostic image uses the older USB transport. Wait for the matched current diagnostic build; no firmware was changed.");
            backupFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlyingThumb", "DiagnosticBackups", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + port);
            Directory.CreateDirectory(backupFolder);
            var tablePath = Path.Combine(backupFolder, "partition-table.bin");
            var tableRead = await RunFlasherCommand(flasher, port, "no_reset", "no_reset", TimeSpan.FromMinutes(1), "read_flash", "0x8000", "0x1000", tablePath);
            if (tableRead.ExitCode != 0) throw new InvalidOperationException("Cannot read the installed partition table. " + tableRead.Output);
            DiagnosticReport.ValidatePartitionLayout(File.ReadAllBytes(tablePath));
            var installedBackup = Path.Combine(backupFolder, "installed-app0.bin");
            bootSelectionBackup = Path.Combine(backupFolder, "boot-selection.bin");
            // Only sectors touched by the temporary image need restoration; preserve the rest in place.
            var touchedBytes = (new FileInfo(diagnostic.Path).Length + 4095) & ~4095L;
            if (touchedBytes <= 0 || touchedBytes > 0x640000) throw new InvalidOperationException("The diagnostic image does not fit the installed application partition.");
            foreach (var backup in new[] { (Offset: "0x10000", Size: "0x" + touchedBytes.ToString("x"), Path: installedBackup), (Offset: "0xe000", Size: "0x2000", Path: bootSelectionBackup), (Offset: "0xff0000", Size: "0x10000", Path: Path.Combine(backupFolder, "original-coredump.bin")) })
            {
                if (backup.Offset == "0x10000" && matchedApplication && new FileInfo(application.Path).Length >= touchedBytes)
                {
                    File.WriteAllBytes(backup.Path, File.ReadAllBytes(application.Path).AsSpan(0, checked((int)touchedBytes)).ToArray());
                    WriteLog("Preserved exact application sectors from the independently verified image.");
                    continue;
                }
                var saved = await RunFlasherCommand(flasher, port, "no_reset", "no_reset", TimeSpan.FromMinutes(3), "read_flash", backup.Offset, backup.Size, backup.Path);
                if (saved.ExitCode != 0 || !File.Exists(backup.Path)) throw new InvalidOperationException("Could not preserve installed firmware before testing. " + saved.Output);
            }
            normalApplication = installedBackup;
            session = DiagnosticSession.Create(backupFolder, expectedMac!);
            WriteLog("Diagnostic recovery backup: " + backupFolder);
            diagnosticInstalled = true; // Every following mutation requires restoration, even a partial flash write.
            session.Phase = "Testing; restoration required"; session.Save(backupFolder);
            var clearOldResult = await RunFlasherCommand(flasher, port, "no_reset", "no_reset", TimeSpan.FromMinutes(1), "erase_region", "0xff0000", "0x10000");
            if (clearOldResult.ExitCode != 0) throw new InvalidOperationException("Could not clear previous diagnostic results before starting. " + clearOldResult.Output);
            summary.Text = "Loading the active hardware test...";
            WriteLog("USB diagnostic: loading temporary active hardware test...");
            var writeDiagnostic = await RunFlasherCommand(flasher, port, "default_reset", "no_reset", TimeSpan.FromMinutes(3),
                "write_flash", "0x10000", diagnostic.Path);
            if (writeDiagnostic.ExitCode != 0) throw new InvalidOperationException("The temporary hardware test could not be loaded.\n\n" + writeDiagnostic.Output);
            diagnosticInstalled = true;
            var clearBootSelection = await RunFlasherCommand(flasher, port, "default_reset", "no_reset", TimeSpan.FromMinutes(1),
                "erase_region", "0xe000", "0x2000");
            if (clearBootSelection.ExitCode != 0) throw new InvalidOperationException("The hardware test could not be selected for startup.\n\n" + clearBootSelection.Output);

            summary.Text = "Testing LED pins, Wi-Fi, microSD, and button...";
            WriteLog("USB diagnostic: running GPIO level/short, LED command, Wi-Fi, microSD, and button checks...");
            await StartApplicationWithoutUsbReset(flasher, port);
            // Opening a serial port changes USB line state. Let the bounded test finish
            // before probing ROM, including its optional 15-second network connection test.
            summary.Text = "Waiting for the hardware test to complete...";
            await Task.Delay(TimeSpan.FromSeconds(30));
            if (!await WaitForRecoveryPort(flasher, port, TimeSpan.FromSeconds(150), expectedMac))
                throw new TimeoutException("The active hardware test did not return to USB recovery mode within 150 seconds.");
            port = diagnosticRecoveryPort ?? port;

            var resultFile = Path.Combine(Path.GetTempPath(), "FlyingThumb", "Diagnostics", Guid.NewGuid().ToString("N") + ".bin");
            Directory.CreateDirectory(Path.GetDirectoryName(resultFile)!);
            var readResult = await RunFlasherCommand(flasher, port, "no_reset", "no_reset", TimeSpan.FromMinutes(1),
                "read_flash", "0xff0000", "0x10000", resultFile);
            if (readResult.ExitCode != 0) throw new InvalidOperationException("The Manager could not read the active hardware-test result.\n\n" + readResult.Output);
            var activeReport = ReadDiagnosticResult(resultFile);
            if (!activeReport.Split('\n').Any(line => line.TrimEnd().EndsWith("|SCHEMA|2", StringComparison.Ordinal)))
                throw new InvalidOperationException("The diagnostic firmware is an older version. Install the complete current Manager package before running active tests.");
            try { File.Delete(resultFile); } catch { }
            report.Add("[ACTIVE HARDWARE TEST]");
            report.Add(activeReport);
            report.Add("");
            if (!activeReport.Contains("|DIAGNOSTIC_COMPLETE|", StringComparison.Ordinal))
                throw new InvalidOperationException("The hardware report is incomplete. Its last checkpoint identifies where testing stopped.");
            var activeFailures = DiagnosticReport.AssessActive(activeReport).Where(result => result.Status is "FAIL" or "INCOMPLETE").ToArray();
            if (activeFailures.Length == 0) passed++;

            summary.Text = "Restoring normal Flying Thumb firmware...";
            var restore = await RunFlasherCommand(flasher, port, "no_reset", "no_reset", TimeSpan.FromMinutes(3),
                "write_flash", "0x10000", normalApplication);
            if (restore.ExitCode != 0) throw new InvalidOperationException("Hardware testing finished, but normal firmware could not be restored. Run Install / Recover via USB.\n\n" + restore.Output);
            var restoreBoot = await RunFlasherCommand(flasher, port, "no_reset", "no_reset", TimeSpan.FromMinutes(1), "write_flash", "0xe000", bootSelectionBackup);
            if (restoreBoot.ExitCode != 0) throw new InvalidOperationException("Application restored, but its original boot selection could not be restored. Backup: " + bootSelectionBackup);
            var originalCoredump = Path.Combine(backupFolder, "original-coredump.bin");
            var restoreCoredump = await RunFlasherCommand(flasher, port, "no_reset", "no_reset", TimeSpan.FromMinutes(1), "write_flash", "0xff0000", originalCoredump);
            if (restoreCoredump.ExitCode != 0) throw new InvalidOperationException("Could not restore original crash-report storage. Backup: " + originalCoredump);
            foreach (var verify in new[] { (Offset: "0x10000", Path: normalApplication), (Offset: "0xe000", Path: bootSelectionBackup), (Offset: "0xff0000", Path: originalCoredump) })
            {
                var verified = await RunFlasherCommand(flasher, port, "no_reset", "no_reset", TimeSpan.FromMinutes(3), "verify_flash", verify.Offset, verify.Path);
                if (verified.ExitCode != 0) throw new InvalidOperationException("Restored bytes did not verify at " + verify.Offset + ". Backup: " + verify.Path + "\n" + verified.Output);
            }
            diagnosticInstalled = false;
            session.Phase = "Restored and verified"; session.Save(backupFolder);
            await StartApplicationWithoutUsbReset(flasher, port);

            summary.Text = "Checking restored drive's Windows file access...";
            await Task.Delay(4000);
            try
            {
                var hostTest = await RunUsbHelper(flasher, "--flyingthumb-volume-test", "--mac", expectedMac!);
                report.Add(hostTest.Output);
                if (hostTest.ExitCode != 0) report.Add("[FAIL] Windows file-access test could not finish");
            }
            catch (Exception ex) { report.Add("[FAIL] Windows file-access test: " + ex.Message); }
            summary.Text = "Checking restored drive's network file access...";
            report.AddRange(await TestDiagnosticNetworkFiles(expectedMac!));

            foreach (var line in report) if (!string.IsNullOrWhiteSpace(line)) WriteLog(line);
            var hostFailed = report.Any(line => line.Contains("USB_STORAGE_HOST|FAIL") || line.Contains("USB_STORAGE_CLEANUP|FAIL") || line.Contains("[FAIL] Windows file-access") || line.Contains("NETWORK_FILE_TEST|FAIL") || line.Contains("NETWORK_CLEANUP|FAIL") || line.Contains("NETWORK_COMMIT|FAIL"));
            var conclusion = passed == 5 && !hostFailed
                ? (referenceDiffers ? "Hardware checks found no failures, but installed firmware differs from the reference image. See the full report when troubleshooting startup failures. Original firmware was restored and verified." : "The completed automatic checks found no failures. Skipped and unverified checks are listed separately; this does not certify every hardware circuit. Original firmware was restored and verified.")
                : $"{passed} of 5 diagnostic groups passed. The report identifies the failing GPIO or hardware stage. Normal firmware was restored automatically.";
            ShowDiagnosticReport(conclusion, string.Join(Environment.NewLine, report), passed == 5 && !hostFailed);
            summary.Text = $"USB diagnostics finished: {passed}/5 groups passed";
        }
        catch (Exception ex)
        {
            diagnosticError = ex.Message;
            report.Add("[FAIL] Diagnostic workflow: " + ex.Message);
            WriteLog("USB diagnostics FAILED - " + ex.Message);
            summary.Text = "USB diagnostics failed";
        }
        finally
        {
            if (diagnosticInstalled && normalApplication.Length > 0)
            {
                try
                {
                    summary.Text = "Recovering normal firmware after the interrupted test...";
                    if (await WaitForRecoveryPort(flasher, port, TimeSpan.FromSeconds(10), expectedMac))
                    {
                        port = diagnosticRecoveryPort ?? port;
                        var restore = await RunFlasherCommand(flasher, port, "no_reset", "no_reset", TimeSpan.FromMinutes(3),
                            "write_flash", "0x10000", normalApplication);
                        if (restore.ExitCode == 0)
                        {
                            var boot = await RunFlasherCommand(flasher, port, "no_reset", "no_reset", TimeSpan.FromMinutes(1), "write_flash", "0xe000", bootSelectionBackup);
                            var crash = await RunFlasherCommand(flasher, port, "no_reset", "no_reset", TimeSpan.FromMinutes(1), "write_flash", "0xff0000", Path.Combine(backupFolder, "original-coredump.bin"));
                            if (boot.ExitCode == 0 && crash.ExitCode == 0)
                            {
                                diagnosticInstalled = false;
                                if (session is not null) { session.Phase = "Restored after interruption"; session.Save(backupFolder); }
                                report.Add("[PASS] Original firmware, boot selection and crash-report storage restored.");
                                await StartApplicationWithoutUsbReset(flasher, port);
                            }
                            else WriteLog("RESTORATION FAILED: " + boot.Output + " Backup: " + bootSelectionBackup);
                        }
                        else WriteLog("RESTORATION FAILED: " + restore.Output + " Backup: " + normalApplication);
                    }
                    else report.Add("[FAIL] Automatic restoration could not reach recovery mode. Preserve backup: " + backupFolder);
                }
                catch (Exception restoreError) { WriteLog("RESTORATION FAILED: " + restoreError.Message + " Backup: " + normalApplication); }
            }
            SetBusy(false);
            if (diagnosticError is not null)
            {
                var restoration = diagnosticInstalled ? "Automatic restoration is incomplete. Backup: " + backupFolder : "Original firmware retained or restored.";
                ShowDiagnosticReport("Diagnostics did not finish. " + restoration, string.Join(Environment.NewLine, report), false);
                summary.Text = "Diagnostics incomplete";
            }
        }
    }

    static string BundledFirmwareVersion()
    {
        try
        {
            var versionFile = Path.Combine(AppContext.BaseDirectory, "firmware-version.txt");
            return File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "unknown";
        }
        catch { return "unknown"; }
    }

    async Task<(string Path, string Version, string Source)> ResolveRecoveryImage(bool screenless)
    {
        const string imageName = "FlyingThumb-v2-full.bin";
        const string versionName = "firmware-version.txt";
        var localImage = Path.Combine(AppContext.BaseDirectory, imageName);
        try
        {
            summary.Text = "Checking for the latest recovery firmware...";
            var manifest = await UpdateService.GetLatestAsync();
            var asset = RecoveryFor(manifest, screenless);
            if (File.Exists(localImage) && UpdateService.IsNewer(BundledFirmwareVersion(), asset.Version))
                return (localImage, BundledFirmwareVersion(), "newer bundled image");
            var downloaded = await UpdateService.DownloadVerifiedAsync(asset, imageName);
            WriteLog($"Downloaded and verified USB recovery firmware {asset.Version}.");
            try
            {
                File.Copy(downloaded, localImage, true);
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, versionName), asset.Version);
            }
            catch (Exception ex) { WriteLog("Could not cache the verified recovery image locally - " + ex.Message); }
            return (downloaded, asset.Version, "latest verified download");
        }
        catch (Exception ex)
        {
            if (!File.Exists(localImage)) throw new InvalidOperationException("The latest recovery firmware could not be downloaded and no bundled recovery image is available.", ex);
            var versionFile = Path.Combine(AppContext.BaseDirectory, versionName);
            var version = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "unknown";
            WriteLog($"Latest recovery check unavailable; using bundled firmware {version} - {ex.Message}");
            return (localImage, version, "bundled offline image");
        }
    }

    async Task<(string Path, string Version, string Source)> ResolveApplicationImage(bool screenless)
    {
        const string imageName = "FlyingThumb-v2-wifi-update.bin";
        var localImage = Path.Combine(AppContext.BaseDirectory, imageName);
        try
        {
            summary.Text = "Checking for the latest diagnostic firmware image...";
            var manifest = await UpdateService.GetLatestAsync();
            var asset = FirmwareFor(manifest, new Device { Hardware = screenless ? "screenless-external-antenna" : "lcd" });
            if (File.Exists(localImage) && UpdateService.IsNewer(BundledFirmwareVersion(), asset.Version))
                return (localImage, BundledFirmwareVersion(), "newer bundled image");
            var downloaded = await UpdateService.DownloadVerifiedAsync(asset, imageName);
            return (downloaded, asset.Version, "latest verified download");
        }
        catch (Exception ex)
        {
            if (!File.Exists(localImage)) throw new InvalidOperationException("The application firmware image could not be downloaded and no bundled copy is available.", ex);
            return (localImage, BundledFirmwareVersion(), "bundled offline image");
        }
    }

    async Task<string> ResolveDiagnosticHelper()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "FlyingThumbEsptool.exe");
        async Task<bool> Supported(string path)
        {
            if (!File.Exists(path)) return false;
            var start = new ProcessStartInfo(path) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add("--flyingthumb-helper-version");
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            var exited = process.WaitForExitAsync();
            if (await Task.WhenAny(exited, Task.Delay(10000)) != exited) { process.Kill(true); await process.WaitForExitAsync(); return false; }
            await error;
            return process.ExitCode == 0 && (await output).Contains("FLYINGTHUMB_HELPER_PROTOCOL=3", StringComparison.Ordinal);
        }
        if (await Supported(bundled)) return bundled;
        summary.Text = "Updating the USB diagnostic helper...";
        var manifest = await UpdateService.GetLatestAsync();
        var downloaded = await UpdateService.DownloadVerifiedAsync(manifest.Flasher, "FlyingThumbEsptool.exe");
        if (!await Supported(downloaded)) throw new InvalidOperationException("The USB diagnostic helper needs a newer Manager package. Install the complete current package or check for updates.");
        try { File.Copy(downloaded, bundled, true); return bundled; } catch { return downloaded; }
    }

    async Task<(string Path, string Version, string Source)> ResolveDiagnosticImage()
    {
        const string imageName = "FlyingThumb-v2-hardware-diagnostic.bin";
        var localImage = Path.Combine(AppContext.BaseDirectory, imageName);
        if (File.Exists(localImage) && System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(localImage)).Contains("HWCDC_V1", StringComparison.Ordinal)) return (localImage, UpdateService.CurrentManagerVersion, "bundled diagnostic matched to this Manager");
        try
        {
            summary.Text = "Downloading the active hardware test...";
            var manifest = await UpdateService.GetLatestAsync();
            var downloaded = await UpdateService.DownloadVerifiedAsync(manifest.Diagnostic, imageName);
            return (downloaded, manifest.Diagnostic.Version, "latest verified download");
        }
        catch (Exception ex)
        {
            if (!File.Exists(localImage)) throw new InvalidOperationException("The active hardware test could not be downloaded and no bundled copy is available.", ex);
            return (localImage, UpdateService.CurrentManagerVersion, "bundled offline image");
        }
    }

    async void RecoverUsb(object? sender, EventArgs e)
    {
        var flasher = Path.Combine(AppContext.BaseDirectory, "FlyingThumbEsptool.exe");
        if (!File.Exists(flasher))
        {
            SetBusy(true); summary.Text = "Downloading the USB installation helper...";
            try
            {
                var manifest = await UpdateService.GetLatestAsync();
                flasher = await UpdateService.DownloadVerifiedAsync(manifest.Flasher, "FlyingThumbEsptool.exe");
                WriteLog("Downloaded and verified the USB installation helper.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The USB installation helper is unavailable. Download the complete Manager package or try again while connected to the internet.\n\n" + ex.Message, "USB Installation Unavailable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            finally { SetBusy(false); }
        }
        (string Path, string Version, string Source) recovery;
        SetBusy(true);
        try { recovery = await ResolveRecoveryImage(screenless: true); summary.Text = $"Recovery firmware {recovery.Version} ready."; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Recovery Firmware Unavailable", MessageBoxButtons.OK, MessageBoxIcon.Error); SetBusy(false); return; }
        SetBusy(false);
        var image = recovery.Path;
        var before = SerialPorts();
        if (MessageBox.Show("1. Unplug the Flying Thumb Drive completely.\n\n2. Press and keep holding its button.\n\n3. While still holding it, plug it directly into this PC.\n\n4. Keep holding until Windows detects the USB drive, then release.\n\nClick OK afterward.", "USB recovery mode", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        string? port = null; for (var i = 0; i < 20 && port is null; i++) { var now = SerialPorts(); port = now.Except(before, StringComparer.OrdinalIgnoreCase).FirstOrDefault(); if (port is null && now.Length == 1) port = now[0]; if (port is null) await Task.Delay(500); }
        if (port is null)
        {
            var choices = SerialPorts();
            if (choices.Length == 0)
            {
                MessageBox.Show("No USB recovery port appeared.\n\nUnplug the Flying Thumb Drive, hold its button before plugging it directly into this PC, keep holding until Windows detects it, then try again.", "Flying Thumb Drive not detected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            port = await ChooseRecoveryPort(flasher);
            if (string.IsNullOrWhiteSpace(port)) return;
        }
        if (MessageBox.Show($"Install Flying Thumb firmware {recovery.Version} through {port}?\n\nSource: {recovery.Source}\nImage size: {new FileInfo(image).Length:N0} bytes\n\nTF-card files will not be erased.", "Confirm USB recovery", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        SetBusy(true); tabs.SelectedIndex = 1; WriteLog($"USB recovery started on {port}: installing firmware {recovery.Version} from {recovery.Source} ({new FileInfo(image).Length:N0} bytes).");
        try { var start = new ProcessStartInfo(flasher) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true }; foreach (var a in new[] { "--chip", "esp32s3", "--port", port, "--baud", "921600", "write_flash", "0x0", image }) start.ArgumentList.Add(a); using var process = new Process { StartInfo = start }; process.OutputDataReceived += (_, a) => { if (!string.IsNullOrWhiteSpace(a.Data)) WriteLog(a.Data); }; process.ErrorDataReceived += (_, a) => { if (!string.IsNullOrWhiteSpace(a.Data)) WriteLog(a.Data); }; process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new InvalidOperationException($"Flashing engine returned error {process.ExitCode}."); WriteLog($"USB installation completed successfully: firmware {recovery.Version} installed and verified."); MessageBox.Show($"Firmware {recovery.Version} installed successfully. Reconnect the Flying Thumb Drive normally."); }
        catch (Exception ex) { WriteLog("USB recovery FAILED - " + ex.Message); MessageBox.Show("USB installation failed. Repeat the button-hold insertion and try again.\n\n" + ex.Message); }
        finally { summary.Text = "USB recovery finished."; SetBusy(false); }
    }
}

sealed class CheckBoxHeaderCell : DataGridViewColumnHeaderCell
{
    public CheckState HeaderCheckState { get; private set; } = CheckState.Unchecked;
    public event EventHandler? ToggleRequested;

    public void SetState(CheckState state)
    {
        if (HeaderCheckState == state) return;
        HeaderCheckState = state;
        DataGridView?.InvalidateCell(this);
    }

    protected override void OnMouseClick(DataGridViewCellMouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left) ToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void Paint(Graphics graphics, Rectangle clipBounds, Rectangle cellBounds, int rowIndex,
        DataGridViewElementStates dataGridViewElementState, object? value, object? formattedValue, string? errorText,
        DataGridViewCellStyle cellStyle, DataGridViewAdvancedBorderStyle advancedBorderStyle, DataGridViewPaintParts paintParts)
    {
        base.Paint(graphics, clipBounds, cellBounds, rowIndex, dataGridViewElementState, value, formattedValue, errorText, cellStyle, advancedBorderStyle, paintParts);
        var visualState = HeaderCheckState switch
        {
            CheckState.Checked => System.Windows.Forms.VisualStyles.CheckBoxState.CheckedNormal,
            CheckState.Indeterminate => System.Windows.Forms.VisualStyles.CheckBoxState.MixedNormal,
            _ => System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal
        };
        var size = CheckBoxRenderer.GetGlyphSize(graphics, visualState);
        var point = new Point(cellBounds.Left + (cellBounds.Width - size.Width) / 2, cellBounds.Top + (cellBounds.Height - size.Height) / 2);
        CheckBoxRenderer.DrawCheckBox(graphics, point, visualState);
    }
}
static class Prompt
{
    static FlowLayoutPanel Buttons(Button ok, Button cancel)
    {
        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
        ok.AutoSize = cancel.AutoSize = true;
        ok.Padding = cancel.Padding = new Padding(10, 3, 10, 3);
        buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
        return buttons;
    }

    static Form Dialog(string title, Control input, string label, out TableLayoutPanel layout)
    {
        var form = new Form { Text = title, AutoScaleMode = AutoScaleMode.Dpi, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Padding = new Padding(16), Font = new Font("Segoe UI", 10) };
        layout = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0), Padding = new Padding(0), MinimumSize = new Size(360, 0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = label, AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 0, 0, 8) }, 0, 0);
        input.Dock = DockStyle.Fill; input.Margin = new Padding(0); input.MinimumSize = new Size(500, 0); layout.Controls.Add(input, 0, 1);
        form.Controls.Add(layout);
        return form;
    }

    public static string? Show(string label, string title, string initial)
    {
        var input = new TextBox { Text = initial };
        using var form = Dialog(title, input, label, out var layout);
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        layout.Controls.Add(Buttons(ok, cancel), 0, 2); form.AcceptButton = ok; form.CancelButton = cancel;
        return form.ShowDialog() == DialogResult.OK ? input.Text : null;
    }

    public static string? ShowSecret(string label, string title, string initial)
    {
        var input = new TextBox { Text = initial, UseSystemPasswordChar = true };
        using var form = Dialog(title, input, label, out var layout);
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        layout.Controls.Add(Buttons(ok, cancel), 0, 2); form.AcceptButton = ok; form.CancelButton = cancel;
        return form.ShowDialog() == DialogResult.OK ? input.Text : null;
    }
    public static string? Choose(string label, string title, string[] choices, string continueText = "Continue")
    {
        var input = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        input.Items.AddRange(choices); if (input.Items.Count > 0) input.SelectedIndex = 0;
        using var form = Dialog(title, input, label, out var layout);
        var ok = new Button { Text = continueText, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        layout.Controls.Add(Buttons(ok, cancel), 0, 2); form.AcceptButton = ok; form.CancelButton = cancel;
        return form.ShowDialog() == DialogResult.OK ? input.SelectedItem?.ToString() : null;
    }
    public static (string Choice, bool ApplyToAll)? ChooseWithApply(string label, string title, string[] choices, string checkText, bool showApply)
    {
        var choice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
        choice.Items.AddRange(choices); if (choice.Items.Count > 0) choice.SelectedIndex = 0;
        var apply = new CheckBox { Text = checkText, AutoSize = true, Visible = showApply, Margin = new Padding(0, 10, 0, 0) };
        var input = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
        input.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); input.RowStyles.Add(new RowStyle(SizeType.AutoSize)); input.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        input.Controls.Add(choice, 0, 0); input.Controls.Add(apply, 0, 1);
        using var form = Dialog(title, input, label, out var layout);
        var ok = new Button { Text = "Use This Copy", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        layout.Controls.Add(Buttons(ok, cancel), 0, 2); form.AcceptButton = ok; form.CancelButton = cancel;
        if (form.ShowDialog() != DialogResult.OK || choice.SelectedItem is null) return null;
        return (choice.SelectedItem.ToString()!, apply.Checked);
    }
}
