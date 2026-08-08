using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;

namespace FlyingThumbManager;

public sealed class MainForm : Form
{
    readonly BindingList<Device> devices = [];
    readonly FlyingThumbClient client = new();
    readonly DataGridView deviceGrid = new();
    readonly DataGridView fileGrid = new();

    readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    readonly ToolStripStatusLabel summary = new("Ready");
    readonly Button addButton = new() { Text = "Add files", AutoSize = true };
    readonly Button syncButton = new() { Text = "Sync selected", AutoSize = true };
    readonly Button refreshFilesButton = new() { Text = "Refresh", AutoSize = true };
    readonly Button updateNowButton = new() { Text = "Update Now", AutoSize = true };
    readonly Label updateNotice = new() { AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(8, 8, 12, 8) };
    readonly FlowLayoutPanel updateBanner = new() { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Visible = false, BackColor = Color.FromArgb(255, 244, 204), Padding = new Padding(8, 4, 8, 4), Margin = new Padding(10, 2, 10, 6) };
    readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    readonly SplitContainer split = new();
    readonly System.Windows.Forms.Timer setupNetworkTimer = new() { Interval = 1500 };
    readonly Dictionary<string, List<RemoteFile>> inventories = new(StringComparer.OrdinalIgnoreCase);
    bool busy;
    bool adjustingSplit;
    bool setupPageOpened;
    UpdateManifest? availableUpdate;
    string managementKey = ManagerSettings.LoadKey();

    public MainForm()
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
            Text = "  Flying Thumb  >  Included drives  >  Files        Drop files here to add them",
            AutoSize = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(65, 65, 65),
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(8, 6, 8, 6),
            Margin = new Padding(10, 0, 10, 6),
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
            Padding = new Padding(10, 6, 10, 4)
        };
        foreach (var button in new[] { addButton, syncButton, refreshFilesButton })
        {
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.FlatStyle = FlatStyle.System;
            button.Padding = new Padding(8, 2, 8, 2);
            button.Margin = new Padding(0, 0, 6, 0);
        }
        buttonFlow.Controls.AddRange([addButton, syncButton, refreshFilesButton]);
        updateNowButton.Margin = new Padding(0, 2, 0, 2);
        updateBanner.Controls.Add(updateNotice);
        updateBanner.Controls.Add(updateNowButton);
        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        actions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        actions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        actions.Controls.Add(buttonFlow, 0, 0);
        actions.Controls.Add(dropHint, 0, 1);
        actions.Controls.Add(updateBanner, 0, 2);
        ConfigureDeviceGrid();
        ConfigureFileGrid();
        var fileTab = new TabPage("Files across all drives") { Padding = new Padding(4), BackColor = Color.White };
        fileTab.Controls.Add(fileGrid);
        var activityTab = new TabPage("Activity log") { Padding = new Padding(4), BackColor = Color.White };
        activityTab.Controls.Add(log);
        tabs.TabPages.Add(fileTab); tabs.TabPages.Add(activityTab);

        split.Dock = DockStyle.Fill; split.Orientation = Orientation.Horizontal; split.SplitterWidth = 6;
        split.Panel1.Padding = new Padding(10, 0, 10, 5); split.Panel1.Controls.Add(deviceGrid);
        split.Panel2.Padding = new Padding(10, 5, 10, 5); split.Panel2.Controls.Add(tabs);
        var status = new StatusStrip { BackColor = Color.FromArgb(245, 245, 245), SizingGrip = true }; status.Items.Add(summary);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(menu, 0, 0); root.Controls.Add(actions, 0, 1); root.Controls.Add(split, 0, 2); root.Controls.Add(status, 0, 3);
        Controls.Add(root); MainMenuStrip = menu;

        addButton.Click += async (_, _) => await ChooseAndAddFiles();
        syncButton.Click += async (_, _) => await SyncAcrossDevices();
        refreshFilesButton.Click += async (_, _) => await RefreshFileMatrix();
        updateNowButton.Click += async (_, _) => await InstallAvailableUpdates();
        DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += async (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) await AddFiles(paths.Where(File.Exists).ToArray()); };
        split.SizeChanged += (_, _) => ApplyResponsiveSplit();
        setupNetworkTimer.Tick += (_, _) => CheckForSetupNetwork();
        Shown += async (_, _) => { await RefreshDevices(); ApplyResponsiveSplit(); setupNetworkTimer.Start(); CheckForSetupNetwork(); };
        FormClosing += (_, _) => ManagerSettings.SaveWindowSize(WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size, WindowState == FormWindowState.Maximized);
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
        OpenSetupPage(false);
    }

    void OpenSetupPage(bool requireConnection)
    {
        if (requireConnection && !IsSetupNetworkConnected())
        {
            MessageBox.Show(this, "First connect Windows Wi-Fi to the FlyingThumb setup network, then try again.", "Flying Thumb Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("http://192.168.77.1/") { UseShellExecute = true });
            summary.Text = "Flying Thumb Setup opened in your browser";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open the setup page. Open http://192.168.77.1 in your browser.\n\n" + ex.Message, "Flying Thumb Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        MessageBox.Show(this, "TF card unavailable on: " + string.Join(", ", unavailable) + ".\n\nCheck that the card is inserted, then restart the drive and refresh devices.", "Sync Cannot Start", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        const int fileFloor = 160;
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
        file.DropDownItems.Add(Item("Find Drives", async (_, _) => await RefreshDevices(), Keys.F5));
        file.DropDownItems.Add(Item("Refresh File View", async (_, _) => await RefreshFileMatrix()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("Install / Recover via USB...", RecoverUsb));
        file.DropDownItems.Add(Item("Check for Updates...", async (_, _) => await CheckForUpdates(true)));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("Exit", (_, _) => Close()));
        var device = new ToolStripMenuItem("Devices");
        device.DropDownItems.Add(Item("Open Setup Page", (_, _) => OpenSetupPage(true)));
        device.DropDownItems.Add(new ToolStripSeparator());
        device.DropDownItems.Add(Item("Select All", (_, _) => SelectAll(true)));
        device.DropDownItems.Add(Item("Select None", (_, _) => SelectAll(false)));
        device.DropDownItems.Add(new ToolStripSeparator());
        device.DropDownItems.Add(Item("Rename Selected Drive...", RenameDevice));
        var settings = new ToolStripMenuItem("Settings");
        settings.DropDownItems.Add(Item("Shop Management Key...", EditShopKey));
        menu.Items.AddRange([file, device, settings]);
        return menu;
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
        fileGrid.Dock = DockStyle.Fill; fileGrid.AllowUserToAddRows = false; fileGrid.AllowUserToDeleteRows = false; fileGrid.ReadOnly = true;
        fileGrid.RowHeadersVisible = false; fileGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; fileGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        ApplyExplorerGridStyle(fileGrid);
    }

    string Key => managementKey;
    Device[] SelectedDevices() { deviceGrid.EndEdit(); return devices.Where(x => x.Selected).ToArray(); }
    void SelectAll(bool selected) { foreach (var d in devices) d.Selected = selected; deviceGrid.Refresh(); RenderFileMatrix(); }
    void WriteLog(string message) { if (InvokeRequired) { BeginInvoke(() => WriteLog(message)); return; } log.AppendText($"{DateTime.Now:t}  {message}{Environment.NewLine}"); }
    void SetBusy(bool value)
    {
        busy = value;
        addButton.Enabled = syncButton.Enabled = refreshFilesButton.Enabled = updateNowButton.Enabled = !value;
        UseWaitCursor = value;
        Cursor = value ? Cursors.WaitCursor : Cursors.Default;
        deviceGrid.UseWaitCursor = value;
        fileGrid.UseWaitCursor = value;
        if (!value)
        {
            deviceGrid.Cursor = Cursors.Default;
            fileGrid.Cursor = Cursors.Default;
        }
    }
    void SetStatus(Device device, string status) { if (InvokeRequired) { BeginInvoke(() => SetStatus(device, status)); return; } device.Status = status; deviceGrid.Refresh(); summary.Text = $"{device.Name}: {status}"; }
    static string FormatSize(long value) => value >= 1L << 30 ? $"{value / (double)(1L << 30):0.0} GB" : value >= 1L << 20 ? $"{value / (double)(1L << 20):0.0} MB" : value >= 1L << 10 ? $"{value / 1024d:0.0} KB" : $"{value} B";

    void RecordUploadedFile(Device device, string name, long size)
    {
        if (!inventories.TryGetValue(device.Id, out var files)) inventories[device.Id] = files = [];
        var fileName = Path.GetFileName(name);
        var existing = files.FirstOrDefault(f => f.Type == "file" && string.Equals(Path.GetFileName(f.Name), fileName, StringComparison.OrdinalIgnoreCase));
        if (existing is null) files.Add(new RemoteFile { Name = "/" + fileName, Size = size, Type = "file" });
        else { existing.Name = "/" + fileName; existing.Size = size; }
    }

    async Task RefreshDevices()
    {
        if (busy) return; SetBusy(true); summary.Text = "Searching the local network..."; WriteLog("Searching for Flying Thumb drives...");
        try { var found = await DeviceDiscovery.FindAsync(TimeSpan.FromSeconds(2)); ReplaceDevices(found); summary.Text = $"Found {found.Count} drive{(found.Count == 1 ? "" : "s")}"; WriteLog(summary.Text + "."); }
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
            try { inventories[d.Id] = await client.ListAsync(d, Key); SetStatus(d, d.IsSimulated ? "Simulated" : "Ready"); }
            catch (Exception ex) { inventories[d.Id] = []; SetStatus(d, "Unavailable"); WriteLog($"{d.Name}: could not read files - {ex.Message}"); }
        }));
        SetBusy(false);
        RenderFileMatrix();
    }

    void RenderFileMatrix()
    {
        var shown = devices.Where(d => d.Selected).ToArray();
        fileGrid.SuspendLayout();
        fileGrid.Columns.Clear();
        fileGrid.Rows.Clear();
        fileGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "File", FillWeight = 150 });
        fileGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Overall", FillWeight = 65 });
        foreach (var d in shown) fileGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = d.Name, FillWeight = 75 });
        var names = shown.Where(d => inventories.ContainsKey(d.Id)).SelectMany(d => inventories[d.Id]).Where(x => x.Type == "file").Select(x => Path.GetFileName(x.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var entries = shown.Select(d => inventories.TryGetValue(d.Id, out var files) ? files.FirstOrDefault(f => string.Equals(Path.GetFileName(f.Name), name, StringComparison.OrdinalIgnoreCase)) : null).ToArray();
            var sizes = entries.Where(x => x is not null).Select(x => x!.Size).Distinct().ToArray();
            var state = sizes.Length > 1 ? "CONFLICT" : entries.All(x => x is not null) ? "On every included drive" : $"On {entries.Count(x => x is not null)}/{shown.Length}";
            var cells = new List<object> { name, state };
            cells.AddRange(entries.Select(x => x is null ? "-" : $"Yes - {FormatSize(x.Size)}"));
            var row = fileGrid.Rows[fileGrid.Rows.Add(cells.ToArray())];
            if (sizes.Length > 1) row.DefaultCellStyle.BackColor = Color.MistyRose;
        }
        fileGrid.ResumeLayout();
        tabs.SelectedIndex = 0;
        summary.Text = shown.Length == 0 ? "No drives included" : $"Showing {fileGrid.Rows.Count} unique file(s) across {shown.Length} included drive(s)";
    }

    async Task ChooseAndAddFiles()
    {
        using var picker = new OpenFileDialog { Multiselect = true, Title = "Choose files to add" };
        if (picker.ShowDialog(this) == DialogResult.OK) await AddFiles(picker.FileNames);
    }

    async Task AddFiles(string[] paths)
    {
        if (busy || paths.Length == 0) return;
        var targets = SelectedDevices();
        if (targets.Length == 0) { MessageBox.Show("Select at least one drive first."); return; }
        if (!EnsureStorageAvailable(targets) || !EnsureManagementKey(targets)) return;

        SetBusy(true);
        tabs.SelectedIndex = 1;
        WriteLog($"Adding {paths.Length} file(s) to {targets.Length} selected drive(s) as one batch...");
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batchMode = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var gate = new object();

        foreach (var device in targets)
        {
            try { batchMode[device.Id] = await client.BeginFileBatchAsync(device, Key); }
            catch (Exception ex)
            {
                blocked.Add(device.Id); failed.Add(device.Id);
                errors.Add($"{device.Name} / prepare transfer: {ex.Message}");
                SetStatus(device, "Could not prepare transfer");
            }
        }

        await Task.WhenAll(targets.Where(d => !blocked.Contains(d.Id)).Select(async device =>
        {
            foreach (var path in paths)
            {
                var name = Path.GetFileName(path);
                SetStatus(device, $"Adding {name}...");
                try
                {
                    await client.UploadAsync(device, path, Key);
                    RecordUploadedFile(device, name, new FileInfo(path).Length);
                    lock (gate) changed.Add(device.Id);
                    WriteLog($"{device.Name}: added {name}.");
                }
                catch (Exception ex)
                {
                    lock (gate) { failed.Add(device.Id); errors.Add($"{device.Name} / {name}: {ex.Message}"); }
                    SetStatus(device, "File failed; continuing batch...");
                    WriteLog($"{device.Name}: FAILED to add {name} - {ex.Message}");
                }
            }
        }));

        var modernSessions = targets.Where(d => !blocked.Contains(d.Id) && batchMode.GetValueOrDefault(d.Id)).ToArray();
        foreach (var device in modernSessions.Where(d => !d.IsSimulated)) SetStatus(device, "Refreshing USB drive...");
        foreach (var device in modernSessions)
        {
            try { await client.CommitFileBatchAsync(device, Key); }
            catch (Exception ex)
            {
                failed.Add(device.Id); errors.Add($"{device.Name} / USB reconnect: {ex.Message}");
                WriteLog($"{device.Name}: USB reconnect failed - {ex.Message}");
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

        SetBusy(false);
        if (legacyChanged.Length > 0)
        {
            SetBusy(true);
            await WaitForReconnectAndRefresh(legacyChanged, "File transfer");
        }
        foreach (var device in devices.Where(d => modernSessions.Any(m => m.Id == d.Id) && !failed.Contains(d.Id))) SetStatus(device, device.IsSimulated ? "Simulated" : "Ready");
        foreach (var device in devices.Where(d => failed.Contains(d.Id))) SetStatus(device, "Transfer incomplete");
        RenderFileMatrix();

        if (errors.Count == 0)
        {
            summary.Text = $"Added {paths.Length} file(s) to {targets.Length} drive(s)";
            WriteLog("File batch finished successfully; USB refreshed once after all uploads.");
        }
        else
        {
            summary.Text = $"File batch finished with {errors.Count} error(s)";
            MessageBox.Show(this, "The batch continued after individual failures.\n\n" + string.Join("\n", errors.Take(10)) + (errors.Count > 10 ? $"\n\n...and {errors.Count - 10} more." : ""), "Flying Thumb File Transfer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
    async Task SyncAcrossDevices()
    {
        if (busy) return;
        var targets = SelectedDevices();
        if (targets.Length < 2) { MessageBox.Show("Select at least two drives to sync."); return; }
        if (!EnsureStorageAvailable(targets) || !EnsureManagementKey(targets)) return;

        SetBusy(true);
        tabs.SelectedIndex = 1;
        WriteLog($"Building an additive union across {targets.Length} drives...");
        var current = new Dictionary<string, List<RemoteFile>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var device in targets)
            {
                if (inventories.TryGetValue(device.Id, out var cached))
                    current[device.Id] = cached.Select(f => new RemoteFile { Name = f.Name, Size = f.Size, Type = f.Type }).ToList();
                else
                    current[device.Id] = await client.ListAsync(device, Key);
            }
        }
        catch (Exception ex)
        {
            WriteLog("Sync could not start - " + ex.Message);
            summary.Text = "Sync could not read every selected drive";
            SetBusy(false);
            return;
        }

        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batchMode = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var device in targets)
        {
            try { batchMode[device.Id] = await client.BeginFileBatchAsync(device, Key); }
            catch (Exception ex)
            {
                blocked.Add(device.Id); failed.Add(device.Id);
                errors.Add($"{device.Name} / prepare sync: {ex.Message}");
                SetStatus(device, "Could not prepare sync");
            }
        }

        var names = current.Values.SelectMany(x => x).Where(x => x.Type == "file").Select(x => Path.GetFileName(x.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        int copied = 0, conflicts = 0;
        var temp = Path.Combine(Path.GetTempPath(), "FlyingThumb", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            foreach (var name in names)
            {
                var present = targets.Select(d => (Device: d, File: current[d.Id].FirstOrDefault(f => string.Equals(Path.GetFileName(f.Name), name, StringComparison.OrdinalIgnoreCase)))).Where(x => x.File is not null).ToArray();
                if (present.Select(x => x.File!.Size).Distinct().Count() > 1)
                {
                    conflicts++;
                    WriteLog($"CONFLICT skipped: {name} has different sizes on different drives.");
                    continue;
                }

                var missing = targets.Where(d => !blocked.Contains(d.Id) && !present.Any(x => x.Device.Id == d.Id)).ToArray();
                if (missing.Length == 0) continue;
                var local = Path.Combine(temp, name);
                try { await client.DownloadAsync(present[0].Device, name, local); }
                catch (Exception ex)
                {
                    errors.Add($"Could not read {name} from {present[0].Device.Name}: {ex.Message}");
                    WriteLog($"FAILED to stage {name}; continuing sync - {ex.Message}");
                    continue;
                }

                foreach (var destination in missing)
                {
                    SetStatus(destination, $"Syncing {name}...");
                    try
                    {
                        await client.UploadAsync(destination, local, Key);
                        RecordUploadedFile(destination, name, present[0].File!.Size);
                        current[destination.Id].Add(new RemoteFile { Name = "/" + name, Size = present[0].File!.Size, Type = "file" });
                        changed.Add(destination.Id);
                        copied++;
                        SetStatus(destination, "Synced");
                    }
                    catch (Exception ex)
                    {
                        failed.Add(destination.Id);
                        errors.Add($"{destination.Name} / {name}: {ex.Message}");
                        SetStatus(destination, "File failed; continuing sync...");
                        WriteLog($"{destination.Name}: FAILED to copy {name}; continuing - {ex.Message}");
                    }
                }
            }

            var modernSessions = targets.Where(d => !blocked.Contains(d.Id) && batchMode.GetValueOrDefault(d.Id)).ToArray();
            foreach (var device in modernSessions.Where(d => !d.IsSimulated)) SetStatus(device, "Refreshing USB drive...");
            foreach (var device in modernSessions)
            {
                try { await client.CommitFileBatchAsync(device, Key); }
                catch (Exception ex)
                {
                    failed.Add(device.Id); errors.Add($"{device.Name} / USB reconnect: {ex.Message}");
                    WriteLog($"{device.Name}: USB reconnect failed - {ex.Message}");
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

            WriteLog($"Additive sync finished: {copied} file copy operation(s), {conflicts} conflict(s), {errors.Count} error(s). USB refreshed once after the complete batch.");
            SetBusy(false);
            if (legacyChanged.Length > 0)
            {
                SetBusy(true);
                await WaitForReconnectAndRefresh(legacyChanged, "Sync");
            }
            foreach (var device in devices.Where(d => modernSessions.Any(m => m.Id == d.Id) && !failed.Contains(d.Id))) SetStatus(device, device.IsSimulated ? "Simulated" : "Ready");
            foreach (var device in devices.Where(d => failed.Contains(d.Id))) SetStatus(device, "Sync incomplete");
            RenderFileMatrix();
            summary.Text = errors.Count == 0 ? $"Sync complete - {copied} copies, {conflicts} conflicts" : $"Sync finished with {errors.Count} error(s)";
            if (errors.Count > 0)
                MessageBox.Show(this, "Sync continued after individual failures.\n\n" + string.Join("\n", errors.Take(10)) + (errors.Count > 10 ? $"\n\n...and {errors.Count - 10} more." : ""), "Flying Thumb Sync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
            SetBusy(false);
        }
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

    Device[] DrivesNeedingUpdate(UpdateManifest manifest) => devices.Where(d => !d.IsSimulated && UpdateService.IsNewer(manifest.Firmware.Version, d.Firmware)).ToArray();

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
                if (outdatedDrives.Length > 0) updateNotice.Text += " Drive updates usually take about 5 seconds each.";
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
            ? "\n\nEach drive update usually takes about 5 seconds. Keep the drives powered until they reconnect."
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
                var firmwarePath = await UpdateService.DownloadVerifiedAsync(manifest.Firmware, "FlyingThumb-v2-wifi-update.bin");
                try { File.Copy(firmwarePath, Path.Combine(AppContext.BaseDirectory, "FlyingThumb-v2-wifi-update.bin"), true); } catch (Exception ex) { WriteLog("Could not cache the Wi-Fi update locally - " + ex.Message); }
                if (!string.IsNullOrWhiteSpace(manifest.Recovery.Url))
                {
                    try
                    {
                        var recoveryPath = await UpdateService.DownloadVerifiedAsync(manifest.Recovery, "FlyingThumb-v2-full.bin");
                        File.Copy(recoveryPath, Path.Combine(AppContext.BaseDirectory, "FlyingThumb-v2-full.bin"), true);
                    }
                    catch (Exception ex) { WriteLog("Could not refresh the USB recovery image - " + ex.Message); }
                }

                await Task.WhenAll(outdatedDrives.Select(async device =>
                {
                    try
                    {
                        SetStatus(device, "Installing update...");
                        await client.UpgradeFirmwareAsync(device, firmwarePath, Key);
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
                    await WaitForReconnectAndRefresh(successfulDrives.ToArray(), "Drive update");
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

    async void RecoverUsb(object? sender, EventArgs e)
    {
        var flasher = Path.Combine(AppContext.BaseDirectory, "FlyingThumbEsptool.exe"); var image = Path.Combine(AppContext.BaseDirectory, "FlyingThumb-v2-full.bin");
        if (!File.Exists(flasher) || !File.Exists(image)) { MessageBox.Show("Recovery files are missing. Re-copy the complete manager folder."); return; }
        var before = SerialPorts();
        if (MessageBox.Show("1. Unplug the LILYGO completely.\n\n2. Press and keep holding its button.\n\n3. While still holding it, plug it directly into this PC.\n\n4. Keep holding until Windows detects the USB device (usually 2-5 seconds), then release.\n\nClick OK afterward.", "USB recovery mode", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        string? port = null; for (var i = 0; i < 20 && port is null; i++) { var now = SerialPorts(); port = now.Except(before, StringComparer.OrdinalIgnoreCase).FirstOrDefault(); if (port is null && now.Length == 1) port = now[0]; if (port is null) await Task.Delay(500); }
        if (port is null)
        {
            var choices = SerialPorts();
            if (choices.Length == 0)
            {
                MessageBox.Show("No USB recovery port appeared.\n\nUnplug the LILYGO, hold its button before plugging it directly into this PC, keep holding until Windows detects it, then try again.", "LILYGO not detected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            port = choices.Length == 1 ? choices[0] : Prompt.Choose("Recovery USB port", "Choose USB port", choices);
            if (string.IsNullOrWhiteSpace(port)) return;
        }
        if (MessageBox.Show($"Install complete firmware through {port}?\n\nTF-card files will not be erased.", "Confirm USB recovery", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        SetBusy(true); tabs.SelectedIndex = 1; WriteLog($"USB recovery started on {port}.");
        try { var start = new ProcessStartInfo(flasher) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true }; foreach (var a in new[] { "--chip", "esp32s3", "--port", port, "--baud", "921600", "write_flash", "0x0", image }) start.ArgumentList.Add(a); using var process = new Process { StartInfo = start }; process.OutputDataReceived += (_, a) => { if (!string.IsNullOrWhiteSpace(a.Data)) WriteLog(a.Data); }; process.ErrorDataReceived += (_, a) => { if (!string.IsNullOrWhiteSpace(a.Data)) WriteLog(a.Data); }; process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new InvalidOperationException($"Flashing engine returned error {process.ExitCode}."); WriteLog("USB installation completed successfully."); MessageBox.Show("Installed successfully. Reconnect the LILYGO normally."); }
        catch (Exception ex) { WriteLog("USB recovery FAILED - " + ex.Message); MessageBox.Show("USB installation failed. Repeat the button-hold insertion and try again.\n\n" + ex.Message); }
        finally { SetBusy(false); }
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
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 0, 0, 6) }, 0, 0);
        input.Dock = DockStyle.Fill; input.Margin = new Padding(0); input.MinimumSize = new Size(330, 0); layout.Controls.Add(input, 0, 1);
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
    public static string? Choose(string label, string title, string[] choices)
    {
        var input = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        input.Items.AddRange(choices); if (input.Items.Count > 0) input.SelectedIndex = 0;
        using var form = Dialog(title, input, label, out var layout);
        var ok = new Button { Text = "Continue", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        layout.Controls.Add(Buttons(ok, cancel), 0, 2); form.AcceptButton = ok; form.CancelButton = cancel;
        return form.ShowDialog() == DialogResult.OK ? input.SelectedItem?.ToString() : null;
    }
}