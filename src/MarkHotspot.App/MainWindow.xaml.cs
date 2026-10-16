using System.IO;
using System.Diagnostics;
using Microsoft.Win32;
using Windows.Networking.Connectivity;
using Windows.Devices.WiFi;
using Windows.Networking.NetworkOperators;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using SharpPcap;
using PacketDotNet;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace MarkHotspot;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<AdapterRow> _adapters = new();
    private readonly ObservableCollection<HotspotWifiAdapterRow> _hotspotWifiAdapters = new();
    private readonly ObservableCollection<HotspotWifiAdapterRow> _internetWifiAdapters = new();
    private readonly ObservableCollection<DeviceRow> _devices = new();
    private readonly ObservableCollection<TrafficLimitRow> _trafficLimits = new();
    private readonly Dictionary<string, DeviceRow> _devicesByMac = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _appliedBlockFilters = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _macByIp = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TrafficCounter> _trafficByMac = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _trafficTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _activationTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private ICaptureDevice? _captureDevice;
    private bool _captureStarted;
    private readonly string _dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkHotspot");
    private readonly string _settingsPath;
    private AppSettings _settings = new();
    private bool _changingTheme;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _allowExit;
    private bool _exitInProgress;
    private readonly DispatcherTimer _hotspotStateTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _deviceDiscoveryTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _deviceScanInProgress;
    private bool _npcapInstallAttempted;
    private bool _hotspotOperationInProgress;
    private bool _changingStartupToggle;
    private DateTime _lastDeviceCacheSaveUtc = DateTime.MinValue;
    private readonly bool _startMinimized;

    public MainWindow()
    {
        InitializeComponent();
        _activationTimer.Tick += (_, _) =>
        {
            if (App.ActivationSignal?.WaitOne(0) == true)
                ShowFromTray();
        };
        _activationTimer.Start();
        SourceInitialized += (_, _) => ApplyNativeTitleBarTheme(_settings.IsDarkTheme);
        _startMinimized = Environment.GetCommandLineArgs().Skip(1)
            .Any(arg => string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase));
        InitializeTrayIcon();
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        _settingsPath = Path.Combine(_dataDirectory, "settings.json");
        AdaptersList.ItemsSource = _adapters;
        HotspotAdapterCombo.ItemsSource = _hotspotWifiAdapters;
        InternetAdapterCombo.ItemsSource = _internetWifiAdapters;
        DevicesList.ItemsSource = _devices;
        TrafficLimitsList.ItemsSource = _trafficLimits;
        _trafficTimer.Tick += (_, _) => UpdateTrafficRates();
        _trafficTimer.Start();
        LoadSettings();
        CurrentSettings = _settings;
        RestoreDeviceCache();
        AutoStartToggle.IsChecked = _settings.StartWithWindows;
        try { ApplyStartupRegistration(_settings.StartWithWindows); }
        catch (Exception ex) { SetStatus("No se pudo aplicar el inicio automático: " + ex.Message); }
        InstalledVersionText.Text = "Versión instalada: " + GetInstalledVersionLabel();
        Loaded += MainWindow_Loaded;
        _hotspotStateTimer.Tick += async (_, _) => await RefreshModernHotspotStateAsync();
        _hotspotStateTimer.Start();
        _deviceDiscoveryTimer.Tick += async (_, _) => await ScanDevicesAsync();
        LoadTrafficLimitSettings();
        _changingTheme = true;
        DarkThemeToggle.IsChecked = _settings.IsDarkTheme;
        LightThemeToggle.IsChecked = !_settings.IsDarkTheme;
        _changingTheme = false;
        ApplyTheme(_settings.IsDarkTheme);
        SsidBox.Text = _settings.Ssid;
        PasswordBox.Password = _settings.Password;
        PasswordVisibleBox.Text = _settings.Password;
        RefreshAdapters();
        NetworkChange.NetworkAddressChanged += NetworkChange_NetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += NetworkChange_NetworkAvailabilityChanged;
        Closed += (_, _) =>
        {
            NetworkChange.NetworkAddressChanged -= NetworkChange_NetworkAddressChanged;
            NetworkChange.NetworkAvailabilityChanged -= NetworkChange_NetworkAvailabilityChanged;
            _activationTimer.Stop();
            _trafficTimer.Stop();
            _hotspotStateTimer.Stop();
            try { PersistDeviceCache(); SaveSettings(); } catch { }
            _deviceDiscoveryTimer.Stop();
            StopTrafficCapture();
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_startMinimized)
        {
            ShowInTaskbar = false;
            Hide();
        }
        else
        {
            ShowInTaskbar = true;
            WindowState = WindowState.Maximized;
        }

        _deviceDiscoveryTimer.Start();
        await RefreshHotspotWifiAdaptersAsync();
        await StartModernHotspotOnLaunchAsync();
        await ScanDevicesAsync();
        ApplySavedTrafficPolicies();
    }

    private void InitializeTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Abrir MarkHotspot", null, (_, _) => ShowFromTray());
        menu.Items.Add("Configuración", null, (_, _) => OpenTrayPage(SettingsPage));
        menu.Items.Add("Acerca de", null, (_, _) => OpenTrayPage(AboutPage));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Salir", null, async (_, _) => await ExitApplicationAsync());

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "MarkHotspot",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(
                Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty)
                ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            HideToTray();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowExit) return;
        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        if (_trayIcon is not null)
        {
            _trayIcon.BalloonTipTitle = "MarkHotspot";
            _trayIcon.BalloonTipText = "La aplicación sigue ejecutándose en segundo plano.";
            _trayIcon.ShowBalloonTip(1200);
        }
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Maximized;
        Activate();
    }

    private async Task ExitApplicationAsync()
    {
        if (_exitInProgress) return;
        _exitInProgress = true;
        try
        {
            var result = await StopModernHotspotAsync();
            if (!result.Success)
            {
                var answer = System.Windows.MessageBox.Show(
                    "Windows no confirmó que el hotspot se haya detenido. ¿Quieres salir de MarkHotspot de todos modos?",
                    "MarkHotspot", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    _exitInProgress = false;
                    return;
                }
            }

            _allowExit = true;
            if (_trayIcon is not null) _trayIcon.Visible = false;
            Close();
        }
        catch (Exception ex)
        {
            var answer = System.Windows.MessageBox.Show(
                "No se pudo detener el hotspot: " + ex.Message + "\\n\\n¿Quieres salir de todos modos?",
                "MarkHotspot", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
            {
                _allowExit = true;
                if (_trayIcon is not null) _trayIcon.Visible = false;
                Close();
            }
            else _exitInProgress = false;
        }
    }

    private void ShowPage(System.Windows.Controls.StackPanel page)
    {
        HomePage.Visibility = Visibility.Collapsed;
        DevicesPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        AboutPage.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;

        var active = page == HomePage ? HomeNavButton
            : page == DevicesPage ? DevicesNavButton
            : page == SettingsPage ? SettingsNavButton
            : AboutNavButton;

        var selectedBackground = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                _settings.IsDarkTheme ? "#2A3B59" : "#DBEAFE"));
        var activeForeground = (System.Windows.Media.Brush)Application.Current.Resources["MainTextBrush"];
        var inactiveForeground = (System.Windows.Media.Brush)Application.Current.Resources["MutedBrush"];

        foreach (var button in new[] { HomeNavButton, DevicesNavButton, SettingsNavButton, AboutNavButton })
        {
            button.Background = System.Windows.Media.Brushes.Transparent;
            button.Foreground = inactiveForeground;
            button.FontWeight = System.Windows.FontWeights.Normal;
        }

        active.Background = selectedBackground;
        active.Foreground = activeForeground;
        active.FontWeight = System.Windows.FontWeights.SemiBold;
    }

    private void HomeNav_Click(object sender, RoutedEventArgs e) => ShowPage(HomePage);

    private void LightThemeToggle_Checked(object sender, RoutedEventArgs e) => SetTheme(false);
    private void DarkThemeToggle_Checked(object sender, RoutedEventArgs e) => SetTheme(true);

    private void LightThemeToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_changingTheme || DarkThemeToggle.IsChecked == true) return;
        _changingTheme = true;
        LightThemeToggle.IsChecked = true;
        _changingTheme = false;
        SetTheme(false);
    }

    private void DarkThemeToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_changingTheme || LightThemeToggle.IsChecked == true) return;
        _changingTheme = true;
        DarkThemeToggle.IsChecked = true;
        _changingTheme = false;
        SetTheme(true);
    }

    private void SetTheme(bool dark)
    {
        if (_changingTheme) return;
        _changingTheme = true;
        try
        {
            if (dark) LightThemeToggle.IsChecked = false;
            else DarkThemeToggle.IsChecked = false;
            _settings.IsDarkTheme = dark;
            ApplyTheme(dark);
            ApplyNativeTitleBarTheme(dark);
            SaveSettings();
            ShowPage(CurrentVisiblePage());
        }
        catch (Exception ex) { SetStatus("No se pudo aplicar o guardar el tema: " + ex.Message); }
        finally { _changingTheme = false; }
    }

    private System.Windows.Controls.StackPanel CurrentVisiblePage() =>
        HomePage.Visibility == Visibility.Visible ? HomePage :
        DevicesPage.Visibility == Visibility.Visible ? DevicesPage :
        SettingsPage.Visibility == Visibility.Visible ? SettingsPage : AboutPage;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    private void ApplyNativeTitleBarTheme(bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var enabled = dark ? 1 : 0;
            // Windows 11 uses attribute 20; attribute 19 is the Windows 10 fallback.
            var result = DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int));
            if (result != 0)
                DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private static void ApplyTheme(bool dark)
    {
        SetBrushColor("BackgroundBrush", dark ? "#101319" : "#F3F6FB");
        SetBrushColor("PanelBrush", dark ? "#191E27" : "#FFFFFF");
        SetBrushColor("AccentBrush", dark ? "#3B82F6" : "#2563EB");
        SetBrushColor("MutedBrush", dark ? "#9AA4B2" : "#5B6472");
        SetBrushColor("MainTextBrush", dark ? "#F4F6FA" : "#172033");
        SetBrushColor("SelectionBrush", dark ? "#2A3B59" : "#DBEAFE");
        SetBrushColor("HoverBrush", dark ? "#263244" : "#E8EEF7");
        SetBrushColor("ScrollTrackBrush", dark ? "#17191C" : "#E8EDF4");
        SetBrushColor("ScrollThumbBrush", dark ? "#666B72" : "#A5AFBD");
    }

    private static void SetBrushColor(string key, string hex)
    {
        // Resource brushes declared in XAML may be frozen (read-only).
        // Replace the resource with a fresh brush instead of mutating its Color.
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        Application.Current.Resources[key] = new System.Windows.Media.SolidColorBrush(color);
    }

    private void DevicesNav_Click(object sender, RoutedEventArgs e) => ShowPage(DevicesPage);
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => ShowPage(SettingsPage);
    private void AboutNav_Click(object sender, RoutedEventArgs e) => ShowPage(AboutPage);

    private void OpenTrayPage(System.Windows.Controls.StackPanel page)
    {
        ShowFromTray();
        ShowPage(page);
    }

    private void TogglePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (PasswordBox.Visibility == Visibility.Visible)
        {
            PasswordVisibleBox.Text = PasswordBox.Password;
            PasswordBox.Visibility = Visibility.Collapsed;
            PasswordVisibleBox.Visibility = Visibility.Visible;
            PasswordEyeSlashPath.Visibility = Visibility.Visible;
            PasswordVisibleBox.Focus();
            PasswordVisibleBox.CaretIndex = PasswordVisibleBox.Text.Length;
        }
        else
        {
            PasswordBox.Password = PasswordVisibleBox.Text;
            PasswordVisibleBox.Visibility = Visibility.Collapsed;
            PasswordEyeSlashPath.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Visible;
            PasswordBox.Focus();
        }
    }

    private void LoadSettings()
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new();
                // Migrate settings files created by older versions.
                if (_settings.BlockedDevices.Count == 0)
                {
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.TryGetProperty("Blacklist", out var legacyBlocked))
                        _settings.BlockedDevices = JsonSerializer.Deserialize<List<BlockedDevice>>(legacyBlocked.GetRawText()) ?? new();
                }
            }
            _settings.BlockedDevices ??= new();
            _settings.TrafficLimits ??= new();
        }
        catch (Exception ex) { SetStatus("No se pudo leer la configuración: " + ex.Message); }
    }

    private void RestoreDeviceCache()
    {
        _settings.DeviceCache ??= new List<DeviceCacheEntry>();
        foreach (var cached in _settings.DeviceCache)
        {
            var mac = (cached.Mac ?? "").Trim().Replace('-', ':').ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(mac) || _devicesByMac.ContainsKey(mac)) continue;
            var name = _settings.DeviceNames.TryGetValue(mac, out var savedName) && !string.IsNullOrWhiteSpace(savedName)
                ? savedName : (string.IsNullOrWhiteSpace(cached.Name) ? SafeDeviceNameFromMac(mac) : cached.Name);
            var row = new DeviceRow(cached.Ip ?? "", mac, "Desconectado")
            {
                Name = name,
                TotalUploadBytes = cached.TotalUploadBytes,
                TotalDownloadBytes = cached.TotalDownloadBytes,
                TrafficText = "↑ —   ↓ —"
            };
            _devicesByMac[mac] = row;
            _devices.Add(row);
            _trafficByMac.GetOrAdd(mac, _ => new TrafficCounter()).InitializeTotals(cached.TotalUploadBytes, cached.TotalDownloadBytes);
            if (!string.IsNullOrWhiteSpace(row.Ip)) _macByIp[row.Ip] = mac;
        }
        DevicesCountText.Text = _devices.Count.ToString();
        SyncTrafficLimitRows();
    }

    private void PersistDeviceCache()
    {
        _settings.DeviceCache = _devices.Select(row => new DeviceCacheEntry
        {
            Mac = row.Mac,
            Ip = row.Ip,
            Name = row.Name,
            TotalUploadBytes = row.TotalUploadBytes,
            TotalDownloadBytes = row.TotalDownloadBytes,
            LastSeen = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss")
        }).ToList();
    }

    private void SaveSettings()
    {
        Directory.CreateDirectory(_dataDirectory);
        var temporary = _settingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _settingsPath, true);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshAdapters();
    private async void ScanDevicesButton_Click(object sender, RoutedEventArgs e) => await ScanDevicesAsync();
    private async void BlockDeviceButton_Click(object sender, RoutedEventArgs e) => await ToggleDeviceBlockAsync();
    private void RenameDeviceButton_Click(object sender, RoutedEventArgs e) => RenameSelectedDevice();
    private void LimitDeviceTrafficButton_Click(object sender, RoutedEventArgs e) => ConfigureSelectedDeviceTraffic();

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        try
        {
            var ssid = SsidBox.Text.Trim();
            var password = PasswordBox.Visibility == Visibility.Visible ? PasswordBox.Password : PasswordVisibleBox.Text;
            if (ssid.Length is < 1 or > 32) { SetStatus("El nombre Wi-Fi debe tener entre 1 y 32 caracteres."); return; }
            if (password.Length is < 8 or > 63) { SetStatus("La contraseña debe tener entre 8 y 63 caracteres."); return; }
            if (password.Contains('"')) { SetStatus("La contraseña no puede contener comillas dobles en esta versión."); return; }
            if (HotspotAdapterCombo.SelectedItem is not HotspotWifiAdapterRow selectedHotspot)
            {
                HotspotAdapterStatusText.Text = "El adaptador Wi-Fi elegido para emitir el hotspot no está disponible. Selecciona uno válido.";
                HotspotStateText.Text = "Hotspot Detenido";
                HotspotDetailText.Text = HotspotAdapterStatusText.Text;
                return;
            }
            if (InternetAdapterCombo.SelectedItem is not HotspotWifiAdapterRow selectedInternet)
            {
                InternetAdapterStatusText.Text = "El adaptador elegido para recibir Internet no está disponible. Selecciona uno válido.";
                HotspotStateText.Text = "Hotspot Detenido";
                HotspotDetailText.Text = InternetAdapterStatusText.Text;
                return;
            }

            var adapterSelectionChanged =
                !selectedHotspot.Id.Equals(_settings.SelectedHotspotAdapterId, StringComparison.OrdinalIgnoreCase) ||
                !selectedInternet.Id.Equals(_settings.SelectedInternetAdapterId, StringComparison.OrdinalIgnoreCase);
            if (adapterSelectionChanged && await GetModernHotspotStateAsync() == TetheringOperationalState.On)
            {
                HotspotAdapterStatusText.Text = "Deteniendo el hotspot anterior para aplicar exactamente los adaptadores seleccionados…";
                var stopped = await StopModernHotspotAsync();
                if (!stopped.Success)
                {
                    HotspotStateText.Text = "Hotspot Detenido";
                    HotspotDetailText.Text = "No se cambió ningún adaptador: " + stopped.Message;
                    return;
                }
            }

            // The Start button applies the current combo-box selections directly;
            // users no longer have to press Save first.
            _settings.SelectedHotspotAdapterId = selectedHotspot.Id;
            _settings.SelectedInternetAdapterId = selectedInternet.Id;
            _settings.Ssid = ssid;
            _settings.Password = password;
            SaveSettings();
            HotspotAdapterStatusText.Text = "Adaptador de salida seleccionado: " + selectedHotspot.DisplayName;
            InternetAdapterStatusText.Text = "Fuente de Internet seleccionada: " + selectedInternet.DisplayName;
            HotspotStateText.Text = "Activando Hotspot…";
            HotspotDetailText.Text = "Aplicando los adaptadores elegidos; no se cambiarán automáticamente.";
            var result = await StartModernHotspotAsync();
            if (!result.Success)
            {
                HotspotStateText.Text = "Hotspot Detenido";
                HotspotDetailText.Text = result.Message;
                return;
            }
            await ConfirmHotspotStateAsync();
            await RefreshAdaptersAsync();
            await ScanDevicesAsync();
        }
        catch (Exception ex) { SetStatus("Error al iniciar el punto de acceso: " + ex.Message); }
        finally { StartButton.IsEnabled = true; }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        try
        {
            HotspotStateText.Text = "Hotspot Deteniendo…";
            var result = await StopModernHotspotAsync();
            HotspotStateText.Text = "Hotspot Detenido";
            HotspotDetailText.Text = result.Message;
            await RefreshModernHotspotStateAsync();
        }
        catch (Exception ex) { SetStatus("Error al detener: " + ex.Message); }
        finally { StopButton.IsEnabled = true; }
    }

    private async Task ScanDevicesAsync()
    {
        if (_deviceScanInProgress) return;
        _deviceScanInProgress = true;
        try
        {
            // Check Npcap once per app session. If it is missing, offer the official
            // installer with Windows elevation; never open repeated installers on timer ticks.
            if (!IsNpcapInstalled() && !_npcapInstallAttempted)
            {
                _npcapInstallAttempted = true;
                var installed = await DownloadAndInstallNpcapAsync();
                if (!installed)
                    SetStatus("Npcap no está instalado; la captura de tráfico en tiempo real no estará disponible.");
            }

            var hotspotAddresses = GetHotspotInterfaceAddresses().ToList();
            if (hotspotAddresses.Count == 0)
            {
                DevicesCountText.Text = _devices.Count.ToString();
                return;
            }

            var connectedMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hosted = await RunNetshAsync("wlan show hostednetwork");
            foreach (Match match in Regex.Matches(hosted.Output, @"\b[0-9a-fA-F]{2}(?:[:-][0-9a-fA-F]{2}){5}\b"))
                connectedMacs.Add(match.Value.Replace('-', ':').ToUpperInvariant());

            var found = new Dictionary<string, (string Ip, string Mac)>(StringComparer.OrdinalIgnoreCase);
            foreach (var localAddress in hotspotAddresses)
            {
                var result = await RunProcessAsync("arp.exe", $"-a -N {localAddress}");
                foreach (var device in ParseNeighbors(result.Output))
                {
                    if (!IsInSameSubnet(localAddress, device.Ip)) continue;
                    if (IPAddress.TryParse(device.Ip, out var ip) && ip.Equals(IPAddress.Parse(localAddress))) continue;
                    if (string.IsNullOrWhiteSpace(device.Mac)) continue;
                    found[device.Mac] = (device.Ip, device.Mac);
                    _macByIp[device.Ip] = device.Mac.ToUpperInvariant();
                }
            }

            foreach (var device in found.Values)
            {
                var mac = device.Mac.ToUpperInvariant();
                if (!_devicesByMac.TryGetValue(mac, out var row))
                {
                    var name = await ResolveDeviceNameAsync(device.Ip, mac);
                    if (_settings.DeviceNames.TryGetValue(mac, out var savedName) && !string.IsNullOrWhiteSpace(savedName)) name = savedName;
                    row = new DeviceRow(device.Ip, mac, "Desconectado") { Name = name };
                    _devicesByMac[mac] = row;
                }
                row.Ip = device.Ip;
                var ping = await PingDeviceAsync(device.Ip);
                row.State = connectedMacs.Contains(mac) || ping ? "Conectado" : "Desconectado";
                if (IsBlockedDevice(mac, device.Ip) && _appliedBlockFilters.Add(device.Ip))
                {
                    try
                    {
                        var blockInterface = FindHotspotInterfaceIndex(device.Ip);
                        if (blockInterface is not null) WfpDeviceBlocker.Block(device.Ip, blockInterface.Value);
                        else _appliedBlockFilters.Remove(device.Ip);
                    }
                    catch { _appliedBlockFilters.Remove(device.Ip); }
                }
                var savedLimit = _settings.TrafficLimits.FirstOrDefault(item => item.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));
                if (savedLimit is not null && !savedLimit.Ip.Equals(device.Ip, StringComparison.OrdinalIgnoreCase))
                {
                    savedLimit.Ip = device.Ip;
                    try { ApplyWindowsQosPolicies(savedLimit); SaveSettings(); } catch { }
                }
            }

            foreach (var row in _devices)
                if (!found.ContainsKey(row.Mac) && !connectedMacs.Contains(row.Mac))
                    row.State = "Desconectado";

            // Keep existing DeviceRow objects in place. Clearing and rebuilding the
            // ObservableCollection on every poll resets ListView selection and hover/click state.
            // New devices are inserted in sorted order; existing rows are never re-added.
            foreach (var device in found.Values)
            {
                var mac = device.Mac.ToUpperInvariant();
                if (!_devicesByMac.TryGetValue(mac, out var row)) continue;
                if (!_devices.Contains(row))
                {
                    var insertAt = 0;
                    while (insertAt < _devices.Count &&
                           StringComparer.OrdinalIgnoreCase.Compare(_devices[insertAt].Name, row.Name) <= 0)
                        insertAt++;
                    _devices.Insert(insertAt, row);
                }
            }

            SyncTrafficLimitRows();
            StartTrafficCapture();
            DevicesCountText.Text = _devices.Count.ToString();
            PersistDeviceCache();
            SaveSettings();
            DeviceScanStatusText.Text = "Detección automática activada";
        }
        catch (Exception ex) { SetStatus("Error en la detección automática: " + ex.Message); }
        finally
        {
            _deviceScanInProgress = false;
        }
    }

    private static async Task<string> ResolveDeviceNameAsync(string ip, string mac)
    {
        try
        {
            var task = Dns.GetHostEntryAsync(ip);
            if (await Task.WhenAny(task, Task.Delay(800)) == task)
            {
                var host = (await task).HostName.TrimEnd('.');
                if (!string.IsNullOrWhiteSpace(host) && !host.Equals(ip, StringComparison.OrdinalIgnoreCase))
                    return host.Split('.')[0];
            }
        }
        catch { }
        try
        {
            var result = await RunProcessAsync("nbtstat.exe", $"-A {ip}");
            var match = Regex.Match(result.Output, @"^\s*([^\s<]+)\s+<00>\s+UNIQUE", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value.Trim();
        }
        catch { }
        return SafeDeviceNameFromMac(mac);
    }

    private static string SafeDeviceNameFromMac(string? mac)
    {
        // Some Windows neighbor-table rows can contain a blank or malformed MAC.
        // Never slice from the end unless the string is long enough.
        var normalized = (mac ?? string.Empty).Trim().Replace("-", ":");
        if (normalized.Length == 0)
            return "Dispositivo desconocido";
        var suffix = normalized.Length > 5 ? normalized[^5..] : normalized;
        suffix = suffix.Replace(":", string.Empty);
        return string.IsNullOrWhiteSpace(suffix) ? "Dispositivo desconocido" : "Dispositivo " + suffix;
    }

    private static async Task<bool> PingDeviceAsync(string ip)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, 300);
            return reply.Status == IPStatus.Success;
        }
        catch { return false; }
    }

    private static bool IsNpcapInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\npcap");
            return key is not null;
        }
        catch { return false; }
    }

    private async Task<bool> DownloadAndInstallNpcapAsync()
    {
        string? installerPath = null;
        try
        {
            SetStatus("Npcap no está instalado. Buscando el instalador oficial…");
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MarkHotspot/1.0");

            // Discover the current version from Npcap's official download directory;
            // do not bundle or redistribute the free Npcap installer.
            var index = await client.GetStringAsync("https://npcap.com/dist/");
            var versions = Regex.Matches(index, @"npcap-(\d+\.\d+)\.exe", RegexOptions.IgnoreCase)
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .Select(v => Version.TryParse(v, out var parsed) ? parsed : null)
                .Where(v => v is not null)
                .Cast<Version>()
                .OrderByDescending(v => v)
                .ToList();

            if (versions.Count == 0)
                throw new InvalidOperationException("No se pudo identificar una versión del instalador en la página oficial de Npcap.");

            var version = versions[0];
            var downloadUrl = $"https://npcap.com/dist/npcap-{version}.exe";
            var installDirectory = Path.Combine(_dataDirectory, "Dependencies");
            Directory.CreateDirectory(installDirectory);
            installerPath = Path.Combine(installDirectory, $"npcap-{version}.exe");

            SetStatus($"Descargando Npcap {version} desde el sitio oficial…");
            using (var response = await client.GetAsync(downloadUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var destination = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(destination);
            }

            var fileInfo = new FileInfo(installerPath);
            if (!fileInfo.Exists || fileInfo.Length < 500_000)
                throw new InvalidDataException("El instalador descargado parece incompleto.");

            SetStatus("Descarga completada. Abriendo el instalador oficial de Npcap; sigue los pasos que aparezcan en pantalla…");
            var startInfo = new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "/winpcap_mode=yes"
            };

            using var process = Process.Start(startInfo);
            if (process is null)
                throw new InvalidOperationException("Windows no pudo abrir el instalador de Npcap.");

            await process.WaitForExitAsync();

            if (IsNpcapInstalled())
            {
                SetStatus(process.ExitCode == 3010
                    ? "Npcap se instaló, pero Windows necesita reiniciarse antes de usar la captura de tráfico."
                    : "Npcap está instalado. Continuando con la búsqueda de dispositivos…");
                return true;
            }

            SetStatus(process.ExitCode == 1
                ? "Instalación de Npcap cancelada. Puedes volver a buscar dispositivos cuando quieras."
                : $"El instalador terminó, pero no se detectó el servicio Npcap (código {process.ExitCode}).");
            return false;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            SetStatus("Se canceló el permiso de Windows para instalar Npcap.");
            return false;
        }
        catch (Exception ex)
        {
            SetStatus("No se pudo descargar o instalar Npcap: " + ex.Message);
            return false;
        }
        finally
        {
            // Keep the installer in the app's local data folder for diagnostics/retry.
            // It is not embedded in or redistributed with MarkHotspot.
        }
    }

    private void StartTrafficCapture()
    {
        if (_captureStarted) return;
        if (!IsNpcapInstalled())
        {
            SetStatus("Npcap no está instalado; no se puede capturar tráfico en tiempo real.");
            return;
        }

        try
        {
            var hotspotNics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                    (n.Name.Contains("Local Area Connection*", StringComparison.OrdinalIgnoreCase) ||
                     n.Name.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) ||
                     n.Description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) ||
                     n.Description.Contains("Hosted Network", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var devices = CaptureDeviceList.Instance;
            var candidate = devices.FirstOrDefault(d => hotspotNics.Any(n =>
                (!string.IsNullOrWhiteSpace(n.Id) && d.Name.Contains(n.Id.Trim('{', '}'), StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(d.Description) && !string.IsNullOrWhiteSpace(n.Description) &&
                 (d.Description.Contains(n.Description, StringComparison.OrdinalIgnoreCase) ||
                  n.Description.Contains(d.Description, StringComparison.OrdinalIgnoreCase)))));
            if (candidate is null)
            {
                SetStatus("Npcap está instalado, pero no se encontró una interfaz de captura que coincida con el hotspot.");
                return;
            }

            candidate.OnPacketArrival += TrafficPacketArrived;
            candidate.Open(DeviceModes.Promiscuous, 1000);
            candidate.StartCapture();
            _captureDevice = candidate;
            _captureStarted = true;
        }
        catch (Exception ex)
        {
            StopTrafficCapture();
            SetStatus("No se pudo iniciar la captura de tráfico: " + ex.Message);
        }
    }

    private void StopTrafficCapture()
    {
        try
        {
            if (_captureDevice is not null)
            {
                _captureDevice.OnPacketArrival -= TrafficPacketArrived;
                if (_captureStarted) _captureDevice.StopCapture();
                _captureDevice.Close();
            }
        }
        catch { }
        _captureDevice = null;
        _captureStarted = false;
    }

    private void TrafficPacketArrived(object sender, PacketCapture capture)
    {
        try
        {
            var raw = capture.GetPacket();
            var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
            var ipv4 = packet.Extract<IPv4Packet>();
            if (ipv4 is null) return;
            var sourceIp = ipv4.SourceAddress.ToString();
            var destinationIp = ipv4.DestinationAddress.ToString();
            if (_macByIp.TryGetValue(sourceIp, out var uploadMac))
                _trafficByMac.GetOrAdd(uploadMac, _ => new TrafficCounter()).AddUpload(raw.Data.Length);
            if (_macByIp.TryGetValue(destinationIp, out var downloadMac))
                _trafficByMac.GetOrAdd(downloadMac, _ => new TrafficCounter()).AddDownload(raw.Data.Length);
        }
        catch { }
    }

    private void UpdateTrafficRates()
    {
        foreach (var row in _devices)
        {
            if (_trafficByMac.TryGetValue(row.Mac, out var counter))
            {
                row.TrafficText = $"↑ {FormatRate(counter.TakeUpload())}   ↓ {FormatRate(counter.TakeDownload())}";
                row.TotalUploadBytes = counter.TotalUpload;
                row.TotalDownloadBytes = counter.TotalDownload;
            }
            else
                row.TrafficText = _captureStarted ? "↑ 0 B/s   ↓ 0 B/s" : "↑ —   ↓ —";

            var limitRow = _trafficLimits.FirstOrDefault(x => x.Mac.Equals(row.Mac, StringComparison.OrdinalIgnoreCase));
            if (limitRow is not null)
            {
                limitRow.TrafficText = row.TrafficText;
                limitRow.TotalUploadBytes = row.TotalUploadBytes;
                limitRow.TotalDownloadBytes = row.TotalDownloadBytes;
            }
        }
        if (DateTime.UtcNow - _lastDeviceCacheSaveUtc >= TimeSpan.FromSeconds(10))
        {
            try { PersistDeviceCache(); SaveSettings(); _lastDeviceCacheSaveUtc = DateTime.UtcNow; } catch { }
        }
    }

    private static string FormatRate(long bytesPerSecond) =>
        bytesPerSecond >= 1024 * 1024 ? $"{bytesPerSecond / 1024d / 1024d:0.0} MB/s" :
        bytesPerSecond >= 1024 ? $"{bytesPerSecond / 1024d:0.0} KB/s" : $"{bytesPerSecond} B/s";

    private sealed class TrafficCounter
    {
        private long _upload;
        private long _download;
        private long _totalUpload;
        private long _totalDownload;
        public long TotalUpload => Interlocked.Read(ref _totalUpload);
        public long TotalDownload => Interlocked.Read(ref _totalDownload);
        public void InitializeTotals(long upload, long download)
        {
            Interlocked.Exchange(ref _totalUpload, Math.Max(0, upload));
            Interlocked.Exchange(ref _totalDownload, Math.Max(0, download));
        }
        public void AddUpload(long bytes) { Interlocked.Add(ref _upload, bytes); Interlocked.Add(ref _totalUpload, bytes); }
        public void AddDownload(long bytes) { Interlocked.Add(ref _download, bytes); Interlocked.Add(ref _totalDownload, bytes); }
        public long TakeUpload() => Interlocked.Exchange(ref _upload, 0);
        public long TakeDownload() => Interlocked.Exchange(ref _download, 0);
    }

    private static IEnumerable<string> GetHotspotInterfaceAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            var name = nic.Name;
            var description = nic.Description;
            var looksLikeHotspot = name.Contains("Local Area Connection", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Conexión de área local", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase)
                || description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase)
                || description.Contains("Microsoft Wi-Fi Direct", StringComparison.OrdinalIgnoreCase)
                || description.Contains("Hosted Network", StringComparison.OrdinalIgnoreCase);
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                var local = unicast.Address.ToString();
                // Windows' usual Mobile Hotspot subnet is 192.168.137.0/24.
                if (local == "192.168.137.1" || (looksLikeHotspot && IsPrivateIPv4(local)))
                    yield return local;
            }
        }
    }

    private static bool IsPrivateIPv4(string value)
    {
        if (!IPAddress.TryParse(value, out var address)) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    private static bool IsInSameSubnet(string localAddress, string candidateAddress)
    {
        if (!IPAddress.TryParse(localAddress, out var local) || !IPAddress.TryParse(candidateAddress, out var candidate)) return false;
        var a = local.GetAddressBytes();
        var b = candidate.GetAddressBytes();
        // Mobile Hotspot commonly uses /24; this prevents unrelated LAN neighbors from appearing.
        return a.Length == 4 && b.Length == 4 && a[0] == b[0] && a[1] == b[1] && a[2] == b[2];
    }

    private static List<DeviceRow> ParseNeighbors(string output)
    {
        var rows = new Dictionary<string, DeviceRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            var ipMatch = Regex.Match(line, @"\b(?:\d{1,3}\.){3}\d{1,3}\b");
            if (!ipMatch.Success || !IPAddress.TryParse(ipMatch.Value, out var ip) || IPAddress.IsLoopback(ip)) continue;
            var macMatch = Regex.Match(line, @"\b[0-9a-fA-F]{2}(?:[:-][0-9a-fA-F]{2}){5}\b");
            var mac = macMatch.Success ? macMatch.Value.Replace('-', ':').ToUpperInvariant() : "";
            if (mac.StartsWith("FF:FF:FF", StringComparison.OrdinalIgnoreCase) || mac == "00:00:00:00:00:00") continue;
            var state = line.Contains("Reachable", StringComparison.OrdinalIgnoreCase) || line.Contains("dynamic", StringComparison.OrdinalIgnoreCase) ? "Activo/reciente" : "En caché";
            rows[ipMatch.Value] = new DeviceRow(ipMatch.Value, mac, state);
        }
        return rows.Values.ToList();
    }

    private void LoadTrafficLimitSettings()
    {
        _settings.TrafficLimits ??= new();
        SyncTrafficLimitRows();
    }

    private void SyncTrafficLimitRows()
    {
        var existing = _trafficLimits.ToDictionary(x => x.Mac, StringComparer.OrdinalIgnoreCase);
        _trafficLimits.Clear();
        foreach (var device in _devices.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!existing.TryGetValue(device.Mac, out var row))
            {
                var saved = _settings.TrafficLimits.FirstOrDefault(x => x.Mac.Equals(device.Mac, StringComparison.OrdinalIgnoreCase));
                row = new TrafficLimitRow(device.Name, device.Ip, device.Mac, saved?.DownloadMbps ?? 10, saved?.UploadMbps ?? 3, device.State, device.TrafficText);
            }
            row.Name = device.Name;
            row.Ip = device.Ip;
            row.State = device.DisplayState;
            row.TrafficText = device.TrafficText;
            row.TotalUploadBytes = device.TotalUploadBytes;
            row.TotalDownloadBytes = device.TotalDownloadBytes;
            _trafficLimits.Add(row);
        }
        if (TrafficEngineStatusText is not null)
            TrafficEngineStatusText.Text = "Las políticas de limitación se guardan en Windows QoS y se restauran al iniciar MarkHotspot.";
    }

    private void ApplyDeviceLimit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: TrafficLimitRow row }) return;
        if (!IPAddress.TryParse(row.Ip, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            SetStatus("No se puede guardar la regla: el dispositivo todavía no tiene una IP IPv4 válida. Busca los dispositivos e inténtalo de nuevo.");
            return;
        }
        try
        {
            var setting = new TrafficLimitSetting { Mac = row.Mac.ToUpperInvariant(), Ip = row.Ip, DownloadMbps = row.DownloadMbps, UploadMbps = row.UploadMbps, DownloadKBps = row.DownloadMbps * 125, UploadKBps = row.UploadMbps * 125, UpdatedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss") };
            ApplyWindowsQosPolicies(setting);
            _settings.TrafficLimits.RemoveAll(x => x.Mac.Equals(row.Mac, StringComparison.OrdinalIgnoreCase));
            _settings.TrafficLimits.Add(setting);
            SaveSettings();
            SetStatus($"Políticas QoS guardadas para {row.Name}: descarga {row.DownloadMbps} Mbps y subida {row.UploadMbps} Mbps.");
            TrafficEngineStatusText.Text = "Las políticas QoS de Windows están configuradas para los dispositivos con límites.";
        }
        catch (Exception ex) { SetStatus("No se pudieron guardar los límites: " + ex.Message); }
    }


    private void RenameSelectedDevice()
    {
        if (DevicesList.SelectedItem is not DeviceRow selected) return;
        var dialog = new Window
        {
            Title = "Cambiar nombre del dispositivo", Owner = this, Width = 430, Height = 235,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)Application.Current.Resources["PanelBrush"],
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["MainTextBrush"]
        };
        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new System.Windows.Controls.TextBlock { Text = "Nombre actual del dispositivo", FontSize = 13, Margin = new Thickness(0,0,0,8) });
        var nameBox = new System.Windows.Controls.TextBox { Text = selected.Name, Padding = new Thickness(10), FontSize = 14 };
        root.Children.Add(nameBox);
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0,20,0,0) };
        var cancel = new System.Windows.Controls.Button { Content = "Cancelar", MinWidth = 95 };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var save = new System.Windows.Controls.Button { Content = "Guardar", MinWidth = 95 };
        save.Click += (_, _) =>
        {
            var newName = nameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(newName) || newName.Length > 64)
            {
                System.Windows.MessageBox.Show(dialog, "Escribe un nombre de entre 1 y 64 caracteres.", "MarkHotspot", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                _settings.DeviceNames[selected.Mac] = newName;
                SaveSettings();
                selected.Name = newName;
                SyncTrafficLimitRows();
                SetStatus($"Nombre guardado para {selected.Mac}: {newName}");
                dialog.DialogResult = true;
            }
            catch (Exception ex) { System.Windows.MessageBox.Show(dialog, "No se pudo guardar el nombre: " + ex.Message, "MarkHotspot", MessageBoxButton.OK, MessageBoxImage.Error); }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); root.Children.Add(buttons); dialog.Content = root;
        dialog.Loaded += (_, _) => { nameBox.Focus(); nameBox.SelectAll(); };
        dialog.ShowDialog();
    }

    private void ApplySavedTrafficPolicies()
    {
        foreach (var setting in _settings.TrafficLimits.ToList())
        {
            var device = _devices.FirstOrDefault(row => row.Mac.Equals(setting.Mac, StringComparison.OrdinalIgnoreCase));
            if (device is not null && IPAddress.TryParse(device.Ip, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                setting.Ip = device.Ip;
            if (!IPAddress.TryParse(setting.Ip, out var savedIp) || savedIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
            try { ApplyWindowsQosPolicies(setting); }
            catch (Exception ex) { SetStatus($"No se pudo reactivar el límite de {setting.Mac}: {Short(ex.Message)}"); }
        }
        try { SaveSettings(); } catch { }
    }

    private static void ApplyWindowsQosPolicies(TrafficLimitSetting setting)
    {
        if (!IPAddress.TryParse(setting.Ip, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidOperationException("El dispositivo no tiene una dirección IPv4 válida para aplicar las políticas QoS.");
        var key = Regex.Replace(setting.Mac, "[^A-Fa-f0-9]", "");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("No se pudo crear el identificador de la política QoS.");
        var upName = "MarkHotspot-UL-" + key;
        var downName = "MarkHotspot-DL-" + key;
        var uploadBits = Math.Max(1L, setting.UploadKBps) * 1024L * 8L;
        var downloadBits = Math.Max(1L, setting.DownloadKBps) * 1024L * 8L;
        var script = $@"
$ErrorActionPreference = 'Stop'
$upName = '{upName}'
$downName = '{downName}'
$ip = '{ip}'
Remove-NetQosPolicy -Name $upName -PolicyStore PersistentStore -Confirm:$false -ErrorAction SilentlyContinue
Remove-NetQosPolicy -Name $downName -PolicyStore PersistentStore -Confirm:$false -ErrorAction SilentlyContinue
New-NetQosPolicy -Name $downName -IPDstAddressMatchCondition $ip -ThrottleRateActionBitsPerSecond {downloadBits} -PolicyStore PersistentStore | Out-Null
New-NetQosPolicy -Name $upName -IPSrcAddressMatchCondition $ip -ThrottleRateActionBitsPerSecond {uploadBits} -PolicyStore PersistentStore | Out-Null
";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo iniciar PowerShell para configurar QoS.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Windows no pudo instalar las políticas QoS: " + Short(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr));
    }

    private void ConfigureSelectedDeviceTraffic()
    {
        if (DevicesList.SelectedItem is not DeviceRow selected) return;
        var saved = _settings.TrafficLimits.FirstOrDefault(x => x.Mac.Equals(selected.Mac, StringComparison.OrdinalIgnoreCase));
        var dialog = new Window
        {
            Title = "Limitar tráfico del dispositivo", Owner = this, Width = 470, Height = 310,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)Application.Current.Resources["PanelBrush"],
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["MainTextBrush"]
        };
        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new System.Windows.Controls.TextBlock { Text = selected.Name, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,16) });
        root.Children.Add(new System.Windows.Controls.TextBlock { Text = "↑  Velocidad máxima de subida (KB/s)", Margin = new Thickness(0,0,0,5) });
        var uploadBox = new System.Windows.Controls.TextBox { Text = (saved?.UploadKBps ?? Math.Max(1, (saved?.UploadMbps ?? 3) * 125)).ToString(), Padding = new Thickness(9), Margin = new Thickness(0,0,0,14) };
        root.Children.Add(uploadBox);
        root.Children.Add(new System.Windows.Controls.TextBlock { Text = "↓  Velocidad máxima de bajada (KB/s)", Margin = new Thickness(0,0,0,5) });
        var downloadBox = new System.Windows.Controls.TextBox { Text = (saved?.DownloadKBps ?? Math.Max(1, (saved?.DownloadMbps ?? 10) * 125)).ToString(), Padding = new Thickness(9) };
        root.Children.Add(downloadBox);
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0,20,0,0) };
        var cancel = new System.Windows.Controls.Button { Content = "Cancelar", MinWidth = 95 };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var saveButton = new System.Windows.Controls.Button { Content = "Guardar", MinWidth = 95 };
        saveButton.Click += (_, _) =>
        {
            if (!int.TryParse(uploadBox.Text.Trim(), out var up) || up < 1 || up > 1000000 ||
                !int.TryParse(downloadBox.Text.Trim(), out var down) || down < 1 || down > 1000000)
            {
                System.Windows.MessageBox.Show(dialog, "Introduce valores enteros entre 1 y 1 000 000 KB/s para subida y bajada.", "MarkHotspot", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                _settings.TrafficLimits.RemoveAll(x => x.Mac.Equals(selected.Mac, StringComparison.OrdinalIgnoreCase));
                var setting = new TrafficLimitSetting
                {
                    Mac = selected.Mac.ToUpperInvariant(), Ip = selected.Ip,
                    UploadKBps = up, DownloadKBps = down,
                    UploadMbps = Math.Clamp((int)Math.Ceiling(up / 125.0), 1, 50),
                    DownloadMbps = Math.Clamp((int)Math.Ceiling(down / 125.0), 1, 100),
                    UpdatedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };
                ApplyWindowsQosPolicies(setting);
                _settings.TrafficLimits.RemoveAll(x => x.Mac.Equals(selected.Mac, StringComparison.OrdinalIgnoreCase));
                _settings.TrafficLimits.Add(setting);
                PersistDeviceCache();
                SaveSettings();
                SyncTrafficLimitRows();
                TrafficEngineStatusText.Text = $"Políticas QoS de Windows instaladas para {selected.Name}: ↑ {up} KB/s, ↓ {down} KB/s.";
                SetStatus($"Se guardaron los límites y se instalaron las políticas QoS de Windows para {selected.Name}: subida {up} KB/s, bajada {down} KB/s.");
                dialog.DialogResult = true;
            }
            catch (Exception ex) { System.Windows.MessageBox.Show(dialog, "No se pudieron guardar los límites: " + ex.Message, "MarkHotspot", MessageBoxButton.OK, MessageBoxImage.Error); }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(saveButton); root.Children.Add(buttons); dialog.Content = root;
        dialog.ShowDialog();
    }

    private static AppSettings? CurrentSettings;
    private static bool IsBlockedDevice(string mac, string ip) =>
        CurrentSettings?.BlockedDevices.Any(b =>
            (!string.IsNullOrWhiteSpace(mac) && b.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(ip) && b.Ip.Equals(ip, StringComparison.OrdinalIgnoreCase))) == true;

    private void DevicesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateBlockButton();
    private void UpdateBlockButton()
    {
        if (BlockDeviceButton is null) return;
        var selected = DevicesList.SelectedItem as DeviceRow;
        BlockDeviceButton.IsEnabled = selected is not null;
        RenameDeviceButton.IsEnabled = selected is not null;
        LimitDeviceTrafficButton.IsEnabled = selected is not null;
        BlockDeviceButton.Content = selected is not null && IsBlockedDevice(selected.Mac, selected.Ip) ? "Desbloquear dispositivo" : "Bloquear dispositivo";
    }

    private async Task ToggleDeviceBlockAsync()
    {
        if (DevicesList.SelectedItem is not DeviceRow selected)
        {
            SetStatus("Selecciona primero un dispositivo detectado.");
            return;
        }

        if (!IPAddress.TryParse(selected.Ip, out var parsedIp) ||
            parsedIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            SetStatus("La dirección IPv4 del dispositivo no es válida.");
            return;
        }

        var matchingEntries = _settings.BlockedDevices.Where(b =>
            b.Ip.Equals(selected.Ip, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(selected.Mac) && b.Mac.Equals(selected.Mac, StringComparison.OrdinalIgnoreCase))).ToList();

        if (matchingEntries.Count > 0)
        {
            try
            {
                foreach (var entry in matchingEntries)
                {
                    WfpDeviceBlocker.Unblock(entry.Ip);
                    _appliedBlockFilters.Remove(entry.Ip);
                }
                WfpDeviceBlocker.Unblock(selected.Ip);
                _appliedBlockFilters.Remove(selected.Ip);
                _settings.BlockedDevices.RemoveAll(b =>
                    b.Ip.Equals(selected.Ip, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(selected.Mac) && b.Mac.Equals(selected.Mac, StringComparison.OrdinalIgnoreCase)));
                SaveSettings();
                var safeName = "MarkHotspot-Block-" + selected.Ip.Replace('.', '-');
                await RunNetshAsync($"advfirewall firewall delete rule name=\"{safeName}-In\"");
                await RunNetshAsync($"advfirewall firewall delete rule name=\"{safeName}-Out\"");
                foreach (var row in _devices) row.RefreshBlockedState();
                UpdateBlockButton();
                SetStatus($"Filtro de red eliminado. {selected.Ip} ya no aparece como bloqueado.");
            }
            catch (Exception ex)
            {
                SetStatus("No se pudo quitar el bloqueo de red: " + ex.Message);
            }
            return;
        }

        var interfaceIndex = FindHotspotInterfaceIndex(selected.Ip);
        if (interfaceIndex is null)
        {
            SetStatus("No se encontró la interfaz del hotspot para este dispositivo. Actualiza la lista y vuelve a intentarlo.");
            return;
        }

        try
        {
            // Drop incoming IPv4 packets at Windows Filtering Platform before they can be forwarded by Internet Connection Sharing.
            WfpDeviceBlocker.Block(selected.Ip, interfaceIndex.Value);
            var entry = new BlockedDevice
            {
                Ip = selected.Ip,
                Mac = selected.Mac,
                AddedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm")
            };
            _settings.BlockedDevices.Add(entry);
            try { SaveSettings(); }
            catch
            {
                _settings.BlockedDevices.Remove(entry);
                WfpDeviceBlocker.Unblock(selected.Ip);
                throw;
            }

            _appliedBlockFilters.Add(selected.Ip);
            foreach (var row in _devices) row.RefreshBlockedState();
            UpdateBlockButton();
            SetStatus($"Bloqueo de red aplicado a {selected.Name} ({selected.Ip}) mediante Windows Filtering Platform.");
        }
        catch (Exception ex)
        {
            SetStatus("No se pudo aplicar el filtro de bloqueo. El dispositivo no se marcará como bloqueado: " + ex.Message);
        }
    }

    private static uint? FindHotspotInterfaceIndex(string clientIp)
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            var ipv4 = adapter.GetIPProperties().GetIPv4Properties();
            if (ipv4 is null) continue;
            foreach (var address in adapter.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                if (IsInSameSubnet(address.Address.ToString(), clientIp))
                    return (uint)ipv4.Index;
            }
        }
        return null;
    }

    private async Task<(int ExitCode, string Output)> RunNetshAsync(string arguments) =>
        await RunProcessAsync("netsh.exe", arguments);

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
        };
        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, (await stdout) + Environment.NewLine + (await stderr));
    }

    private static string Short(string value)
    {
        var text = value.Trim().Replace("\r", " ").Replace("\n", " ");
        return text.Length > 300 ? text[..300] : text;
    }

    private void NetworkChange_NetworkAddressChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => { _ = RefreshAdaptersAsync(); _ = RefreshHotspotWifiAdaptersAsync(); }));
    private void NetworkChange_NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => { _ = RefreshAdaptersAsync(); _ = RefreshHotspotWifiAdaptersAsync(); }));
    private void RefreshAdapters() => _ = RefreshAdaptersAsync();

    private Task RefreshAdaptersAsync()
    {
        try
        {
            var rows = NetworkInterface.GetAllNetworkInterfaces()
                .Select(n => new AdapterRow(n.Name, n.NetworkInterfaceType.ToString(), n.OperationalStatus.ToString(),
                    n.Speed > 0 ? $"{n.Speed / 1_000_000d:0.#} Mbps" : "—"))
                .OrderByDescending(n => n.Status == "Up").ThenBy(n => n.Name).ToList();
            _adapters.Clear();
            foreach (var row in rows) _adapters.Add(row);
            UpdateHotspotSourceText();
        }
        catch (Exception ex) { SetStatus("Error de diagnóstico de red: " + ex.Message); }
        return Task.CompletedTask;
    }

    private async Task RefreshHotspotWifiAdaptersAsync()
    {
        try
        {
            var wirelessInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .ToList();
            var wifiAdapters = await WiFiAdapter.FindAllAdaptersAsync();
            var rows = wifiAdapters.Select(adapter =>
            {
                var adapterId = adapter.NetworkAdapter.NetworkAdapterId;
                var nic = wirelessInterfaces.FirstOrDefault(n => Guid.TryParse(n.Id, out var id) && id == adapterId);
                return nic is null ? null : new HotspotWifiAdapterRow(
                    adapterId.ToString("D"), nic.Name, nic.OperationalStatus.ToString(), adapter.NetworkAdapter);
            }).Where(row => row is not null).Cast<HotspotWifiAdapterRow>()
              .OrderByDescending(row => row.Status.Equals("Up", StringComparison.OrdinalIgnoreCase))
              .ThenBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

            _hotspotWifiAdapters.Clear();
            _internetWifiAdapters.Clear();
            foreach (var row in rows)
            {
                _hotspotWifiAdapters.Add(row);
                _internetWifiAdapters.Add(row);
            }

            var activeProfile = NetworkInformation.GetInternetConnectionProfile();
            var activeSourceId = activeProfile?.NetworkAdapter.NetworkAdapterId.ToString("D");

            // Only choose defaults on a first run. If a previously selected adapter
            // disappears, preserve its ID and fail visibly instead of silently switching
            // the hotspot or Internet source to a different adapter.
            if (string.IsNullOrWhiteSpace(_settings.SelectedInternetAdapterId))
            {
                _settings.SelectedInternetAdapterId =
                    (activeSourceId is not null && rows.Any(row => row.Id.Equals(activeSourceId, StringComparison.OrdinalIgnoreCase)))
                        ? activeSourceId
                        : rows.FirstOrDefault(row => row.Status.Equals("Up", StringComparison.OrdinalIgnoreCase))?.Id ?? "";
            }

            if (string.IsNullOrWhiteSpace(_settings.SelectedHotspotAdapterId))
            {
                var sourceId = _settings.SelectedInternetAdapterId;
                var preferred = rows.FirstOrDefault(row => !row.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase) && row.Status.Equals("Up", StringComparison.OrdinalIgnoreCase))
                    ?? rows.FirstOrDefault(row => !row.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase))
                    ?? rows.FirstOrDefault(row => row.Status.Equals("Up", StringComparison.OrdinalIgnoreCase))
                    ?? rows.FirstOrDefault();
                _settings.SelectedHotspotAdapterId = preferred?.Id ?? "";
            }
            SaveSettings();

            HotspotAdapterCombo.SelectedValue = _settings.SelectedHotspotAdapterId;
            InternetAdapterCombo.SelectedValue = _settings.SelectedInternetAdapterId;
            if (rows.Count == 0)
                HotspotAdapterStatusText.Text = "Windows no detectó adaptadores Wi-Fi compatibles para el hotspot.";
            else if (HotspotAdapterCombo.SelectedItem is null)
                HotspotAdapterStatusText.Text = "El adaptador guardado no está disponible. Conéctalo o selecciona otro y pulsa Guardar.";
            else
                HotspotAdapterStatusText.Text = $"{rows.Count} adaptador(es) Wi-Fi detectado(s). Configura ambos adaptadores y pulsa Guardar.";

            InternetAdapterStatusText.Text = rows.Any(row => row.Id.Equals(_settings.SelectedInternetAdapterId, StringComparison.OrdinalIgnoreCase))
                ? "La fuente de Internet seleccionada se usará para compartir la conexión con el hotspot."
                : "Selecciona la Wi-Fi conectada a Internet.";
            UpdateHotspotSourceText();
        }
        catch (Exception ex)
        {
            HotspotAdapterStatusText.Text = "No se pudieron enumerar los adaptadores Wi-Fi: " + ex.Message;
            UpdateHotspotSourceText();
        }
    }

    private void UpdateHotspotSourceText()
    {
        try
        {
            var selectedSource = _internetWifiAdapters.FirstOrDefault(row => row.Id.Equals(_settings.SelectedInternetAdapterId, StringComparison.OrdinalIgnoreCase));
            var profile = GetSelectedInternetConnectionProfile();
            if (selectedSource is null)
            {
                HotspotSourceText.Text = "Internet recibida desde: selecciona una Wi-Fi de entrada.";
                return;
            }
            HotspotSourceText.Text = "Wi-Fi que recibe Internet: " + selectedSource.DisplayName +
                (profile is null ? " (sin perfil de conexión activo)" : " (perfil detectado)");
        }
        catch { HotspotSourceText.Text = "Internet recibida desde: no se pudo identificar automáticamente."; }
    }

    private HotspotWifiAdapterRow? GetSelectedHotspotWifiAdapter() =>
        _hotspotWifiAdapters.FirstOrDefault(row => row.Id.Equals(_settings.SelectedHotspotAdapterId, StringComparison.OrdinalIgnoreCase));

    private ConnectionProfile? GetSelectedInternetConnectionProfile()
    {
        var selectedId = _settings.SelectedInternetAdapterId;
        var profiles = NetworkInformation.GetConnectionProfiles();
        var selectedProfile = profiles.FirstOrDefault(profile =>
            profile.NetworkAdapter.NetworkAdapterId.ToString("D").Equals(selectedId, StringComparison.OrdinalIgnoreCase));
        if (selectedProfile is not null) return selectedProfile;

        var defaultProfile = NetworkInformation.GetInternetConnectionProfile();
        if (defaultProfile is null) return null;
        if (string.IsNullOrWhiteSpace(selectedId) ||
            defaultProfile.NetworkAdapter.NetworkAdapterId.ToString("D").Equals(selectedId, StringComparison.OrdinalIgnoreCase))
            return defaultProfile;
        return null;
    }

    private NetworkOperatorTetheringManager CreateTetheringManager(ConnectionProfile profile)
    {
        var selectedAdapter = GetSelectedHotspotWifiAdapter();
        return selectedAdapter is null
            ? NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile)
            : NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile, selectedAdapter.NetworkAdapter);
    }

    private async void SaveHotspotAdapterButton_Click(object sender, RoutedEventArgs e)
    {
        if (HotspotAdapterCombo.SelectedItem is not HotspotWifiAdapterRow selected)
        {
            HotspotAdapterStatusText.Text = "Selecciona el adaptador Wi-Fi que emitirá el hotspot.";
            return;
        }
        if (InternetAdapterCombo.SelectedItem is not HotspotWifiAdapterRow selectedInternet)
        {
            InternetAdapterStatusText.Text = "Selecciona la Wi-Fi que recibe Internet.";
            return;
        }
        if (SaveHotspotAdapterButton.IsEnabled == false) return;
        SaveHotspotAdapterButton.IsEnabled = false;
        try
        {
            var oldHotspotId = _settings.SelectedHotspotAdapterId;
            var oldInternetId = _settings.SelectedInternetAdapterId;
            if (selected.Id.Equals(oldHotspotId, StringComparison.OrdinalIgnoreCase) &&
                selectedInternet.Id.Equals(oldInternetId, StringComparison.OrdinalIgnoreCase))
            {
                SaveSettings();
                HotspotAdapterStatusText.Text = "Los adaptadores seleccionados ya están guardados.";
                return;
            }

            var state = await GetModernHotspotStateAsync();
            if (state == TetheringOperationalState.On)
            {
                HotspotAdapterStatusText.Text = "Deteniendo el hotspot para cambiar el adaptador…";
                var stopped = await StopModernHotspotAsync();
                if (!stopped.Success)
                {
                    HotspotAdapterStatusText.Text = "No se cambió el adaptador: " + stopped.Message;
                    return;
                }
            }

            _settings.SelectedHotspotAdapterId = selected.Id;
            _settings.SelectedInternetAdapterId = selectedInternet.Id;
            SaveSettings();
            HotspotAdapterStatusText.Text = state == TetheringOperationalState.On
                ? "Adaptador guardado. Reiniciando el hotspot…"
                : "Adaptador Wi-Fi guardado.";

            if (state == TetheringOperationalState.On)
            {
                var started = await StartModernHotspotAsync();
                HotspotStateText.Text = started.Success ? "Hotspot Activo" : "Hotspot Detenido";
                HotspotDetailText.Text = started.Message;
                HotspotAdapterStatusText.Text = started.Success
                    ? "Adaptador aplicado; el hotspot volvió a iniciarse correctamente."
                    : "El adaptador se guardó, pero Windows no pudo reiniciar el hotspot: " + started.Message;
            }
            UpdateHotspotSourceText();
            InternetAdapterStatusText.Text = "Wi-Fi de entrada guardada: " + selectedInternet.DisplayName;
        }
        catch (Exception ex)
        {
            HotspotAdapterStatusText.Text = "No se pudieron guardar los adaptadores: " + ex.Message;
        }
        finally { SaveHotspotAdapterButton.IsEnabled = true; }
    }

    private async Task StartModernHotspotOnLaunchAsync()
    {
        HotspotStateText.Text = "Activando Hotspot…";
        HotspotDetailText.Text = "Aplicando el nombre y la contraseña guardados en MarkHotspot…";
        var result = await StartModernHotspotAsync();
        HotspotDetailText.Text = result.Message;
        if (result.Success)
        {
            HotspotStateText.Text = "Hotspot Activo";
            await RefreshAdaptersAsync();
        }
        else
        {
            HotspotStateText.Text = "Hotspot Detenido";
        }
    }

    private async Task<(bool Success, string Message)> StartModernHotspotAsync()
    {
        if (_hotspotOperationInProgress) return (false, "Ya hay una operación del hotspot en curso.");
        _hotspotOperationInProgress = true;
        try
        {
            var ssid = _settings.Ssid?.Trim() ?? "";
            var password = _settings.Password ?? "";
            if (ssid.Length is < 1 or > 32)
                return (false, "El nombre Wi-Fi debe tener entre 1 y 32 caracteres.");
            if (password.Length is < 8 or > 63)
                return (false, "La contraseña Wi-Fi debe tener entre 8 y 63 caracteres.");

            var selectedHotspotAdapter = GetSelectedHotspotWifiAdapter();
            if (selectedHotspotAdapter is null)
                return (false, "El adaptador de salida guardado no está disponible. No se usará otro adaptador automáticamente; selecciona el correcto y vuelve a intentarlo.");

            var profile = GetSelectedInternetConnectionProfile();
            if (profile is null)
                return (false, "El adaptador elegido como fuente de Internet no tiene un perfil activo. No se usará otra conexión automáticamente; conecta ese adaptador o selecciona la fuente correcta.");

            var activeSourceId = profile.NetworkAdapter.NetworkAdapterId.ToString("D");
            if (!activeSourceId.Equals(_settings.SelectedInternetAdapterId, StringComparison.OrdinalIgnoreCase))
                return (false, "La conexión detectada no coincide con el adaptador de Internet seleccionado. Se canceló el inicio para evitar usar una conexión distinta.");

            // Stop the obsolete Hosted Network (legacy hotspot) before starting the
            // Windows Mobile Hotspot API. Leaving both mechanisms enabled can create
            // an extra virtual Local Area Connection alongside the modern hotspot.
            await DisableLegacyHostedNetworkAsync();

            var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile, selectedHotspotAdapter.NetworkAdapter);

            // Windows Mobile Hotspot otherwise keeps its own saved SSID/passphrase
            // (often a DESKTOP-* name and an automatically generated password).
            // Stop it before applying the credentials supplied in MarkHotspot.
            if (manager.TetheringOperationalState == TetheringOperationalState.On)
            {
                var stopResult = await manager.StopTetheringAsync();
                if (stopResult.Status != TetheringOperationStatus.Success)
                    return (false, "No se pudo detener el hotspot existente para aplicar el nombre y la contraseña de MarkHotspot: " + stopResult.AdditionalErrorMessage);

                var stopped = await WaitForHotspotStateAsync(TetheringOperationalState.Off, TimeSpan.FromSeconds(20));
                if (!stopped)
                    return (false, "Windows no confirmó que el hotspot anterior se detuviera para cambiar sus credenciales.");
            }

            var configuration = new NetworkOperatorTetheringAccessPointConfiguration
            {
                Ssid = ssid,
                Passphrase = password
            };
            await manager.ConfigureAccessPointAsync(configuration);

            var result = await manager.StartTetheringAsync();
            if (result.Status != TetheringOperationStatus.Success)
                return (false, "Windows no pudo activar el Mobile Hotspot: " + result.AdditionalErrorMessage);

            var confirmed = await WaitForHotspotStateAsync(TetheringOperationalState.On, TimeSpan.FromSeconds(25));
            if (!confirmed)
                return (false, "Windows aceptó la solicitud, pero no confirmó que el Mobile Hotspot esté activo.");

            var sharing = await ConfigureInternetConnectionSharingAsync(profile.NetworkAdapter.NetworkAdapterId);
            if (!sharing.Success)
                return (false, "El hotspot está activo, pero no se pudo configurar el paso de Internet a la interfaz local: " + sharing.Message);

            return (true, $"Hotspot activo con «{ssid}». Adaptador de salida: {selectedHotspotAdapter.DisplayName}. Internet recibida desde: {GetSelectedInternetDisplayName()}.");
        }
        catch (Exception ex)
        {
            return (false, "No se pudo configurar o activar el Mobile Hotspot de Windows: " + ex.Message);
        }
        finally { _hotspotOperationInProgress = false; }
    }

    private string GetSelectedInternetDisplayName()
    {
        var row = _internetWifiAdapters.FirstOrDefault(item => item.Id.Equals(_settings.SelectedInternetAdapterId, StringComparison.OrdinalIgnoreCase));
        return row?.DisplayName ?? "Wi-Fi seleccionada";
    }

    private async Task<(bool Success, string Message)> ConfigureInternetConnectionSharingAsync(Guid sourceAdapterId)
    {
        try
        {
            var sourceNic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(nic => Guid.TryParse(nic.Id, out var id) && id == sourceAdapterId);
            if (sourceNic is null)
                return (false, "No se pudo relacionar el perfil de Internet con su adaptador de red.");

            NetworkInterface? hotspotNic = null;
            var until = DateTime.UtcNow.AddSeconds(18);
            while (DateTime.UtcNow < until)
            {
                hotspotNic = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(IsLikelyHotspotPrivateInterface)
                    .OrderByDescending(nic => nic.GetIPProperties().UnicastAddresses.Any(address => address.Address.ToString() == "192.168.137.1"))
                    .FirstOrDefault();
                if (hotspotNic is not null) break;
                await Task.Delay(600);
            }

            if (hotspotNic is null)
                return (false, "Windows no expuso la interfaz virtual del hotspot (normalmente «Conexión de área local*» o «Local Area Connection*»). No se activó ICS para evitar compartir Internet con una interfaz equivocada.");

            var managerType = Type.GetTypeFromProgID("HNetCfg.HNetShare");
            if (managerType is null)
                return (false, "Windows no expone la API COM HNetCfg para configurar Internet Connection Sharing.");

            object? managerObject = null;
            object? connectionsObject = null;
            object? sourceConnection = null;
            object? targetConnection = null;
            object? sourceConfiguration = null;
            object? targetConfiguration = null;
            try
            {
                managerObject = Activator.CreateInstance(managerType);
                if (managerObject is null) return (false, "No se pudo crear el administrador ICS de Windows.");
                dynamic manager = managerObject;
                connectionsObject = manager.EnumEveryConnection();
                dynamic connections = connectionsObject;

                // Resolve the exact public and private connections first. ICS keeps its
                // previous private-interface binding after the Wi-Fi adapter changes;
                // merely enabling sharing on the new Local Area Connection leaves the
                // old target configured and can strand clients without Internet.
                var allConnections = new List<(object Connection, string Name, string DeviceName)>();
                foreach (var connection in (System.Collections.IEnumerable)connections)
                {
                    dynamic props = manager.NetConnectionProps(connection);
                    string name = Convert.ToString(props.Name) ?? string.Empty;
                    string deviceName = Convert.ToString(props.DeviceName) ?? string.Empty;
                    allConnections.Add((connection, name, deviceName));
                    if (NamesMatchAdapter(name, deviceName, sourceNic)) sourceConnection = connection;
                    if (NamesMatchAdapter(name, deviceName, hotspotNic)) targetConnection = connection;
                }

                if (sourceConnection is null)
                    return (false, $"ICS no encontró la conexión pública correspondiente a «{sourceNic.Name}».");
                if (targetConnection is null)
                    return (false, $"ICS no encontró la interfaz privada del hotspot «{hotspotNic.Name}».");

                // Remove stale ICS roles from old virtual adapters and previous source
                // adapters before binding this run. Do not leave another connection
                // marked PUBLIC or PRIVATE after a user changes either selector.
                foreach (var item in allConnections)
                {
                    bool isSource = ReferenceEquals(item.Connection, sourceConnection);
                    bool isTarget = ReferenceEquals(item.Connection, targetConnection);
                    if (isSource || isTarget) continue;
                    object? staleConfigObject = null;
                    try
                    {
                        dynamic staleConfig = manager.INetSharingConfigurationForINetConnection(item.Connection);
                        staleConfigObject = staleConfig;
                        if (Convert.ToBoolean(staleConfig.SharingEnabled))
                            staleConfig.DisableSharing();
                    }
                    catch { /* Some disconnected/virtual connections do not expose ICS. */ }
                    finally
                    {
                        if (staleConfigObject is not null && Marshal.IsComObject(staleConfigObject))
                            try { Marshal.FinalReleaseComObject(staleConfigObject); } catch { }
                    }
                }

                dynamic sourceConfig = manager.INetSharingConfigurationForINetConnection(sourceConnection);
                dynamic targetConfig = manager.INetSharingConfigurationForINetConnection(targetConnection);
                sourceConfiguration = sourceConfig;
                targetConfiguration = targetConfig;
                // Force a fresh role application even when Windows reports the same
                // role: it clears stale ICS state left behind by adapter switches.
                try { if (Convert.ToBoolean(sourceConfig.SharingEnabled)) sourceConfig.DisableSharing(); } catch { }
                try { if (Convert.ToBoolean(targetConfig.SharingEnabled)) targetConfig.DisableSharing(); } catch { }
                await Task.Delay(800);
                sourceConfig.EnableSharing(0); // SHARINGCONNECTIONTYPE_PUBLIC
                targetConfig.EnableSharing(1); // SHARINGCONNECTIONTYPE_PRIVATE
                return (true, $"ICS: «{sourceNic.Name}» → «{hotspotNic.Name}».");
            }
            finally
            {
                foreach (var comObject in new[] { sourceConfiguration, targetConfiguration, connectionsObject, sourceConnection, targetConnection, managerObject })
                {
                    if (comObject is not null && Marshal.IsComObject(comObject))
                    {
                        try { Marshal.FinalReleaseComObject(comObject); } catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return (false, "Windows rechazó la configuración automática de ICS: " + ex.Message);
        }
    }

    private static bool IsLikelyHotspotPrivateInterface(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
            return false;
        var name = nic.Name;
        var description = nic.Description;
        var localAddress = nic.GetIPProperties().UnicastAddresses.Any(address =>
            address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
            (address.Address.ToString() == "192.168.137.1" || IsPrivateIPv4(address.Address.ToString())));
        var looksVirtual = name.Contains("Local Area Connection", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Conexión de área local", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("Microsoft Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("Hosted Network", StringComparison.OrdinalIgnoreCase);
        return nic.OperationalStatus == OperationalStatus.Up && localAddress && looksVirtual;
    }

    private static bool NamesMatchAdapter(string connectionName, string deviceName, NetworkInterface nic) =>
        connectionName.Equals(nic.Name, StringComparison.OrdinalIgnoreCase) ||
        deviceName.Equals(nic.Description, StringComparison.OrdinalIgnoreCase) ||
        connectionName.Contains(nic.Name, StringComparison.OrdinalIgnoreCase) ||
        nic.Name.Contains(connectionName, StringComparison.OrdinalIgnoreCase) ||
        deviceName.Contains(nic.Description, StringComparison.OrdinalIgnoreCase) ||
        nic.Description.Contains(deviceName, StringComparison.OrdinalIgnoreCase);

    private static void EnsureSharingType(dynamic configuration, int requiredType)
    {
        bool enabled = false;
        int currentType = -1;
        try
        {
            enabled = Convert.ToBoolean(configuration.SharingEnabled);
            if (enabled) currentType = Convert.ToInt32(configuration.SharingConnectionType);
        }
        catch { }

        if (enabled && currentType == requiredType) return;
        if (enabled) configuration.DisableSharing();
        configuration.EnableSharing(requiredType);
    }

    private async Task DisableLegacyHostedNetworkAsync()
    {
        try
        {
            // These commands only affect the legacy WLAN Hosted Network feature.
            // Ignore unsupported/disabled results: modern Mobile Hotspot is controlled
            // exclusively through NetworkOperatorTetheringManager below.
            await RunNetshAsync("wlan stop hostednetwork");
            await RunNetshAsync("wlan set hostednetwork mode=disallow");
        }
        catch
        {
            // Older or restricted Wi-Fi drivers may not expose Hosted Network.
        }
    }

    private async Task<(bool Success, string Message)> StopModernHotspotAsync()
    {
        if (_hotspotOperationInProgress) return (false, "Ya hay una operación del hotspot en curso.");
        _hotspotOperationInProgress = true;
        try
        {
            var profile = GetSelectedInternetConnectionProfile();
            if (profile is null) return (false, "La Wi-Fi seleccionada como fuente de Internet no tiene un perfil activo para controlar el Mobile Hotspot.");
            var manager = CreateTetheringManager(profile);
            if (manager.TetheringOperationalState == TetheringOperationalState.Off)
                return (true, "Windows confirma que el Mobile Hotspot está detenido.");

            var result = await manager.StopTetheringAsync();
            if (result.Status != TetheringOperationStatus.Success)
                return (false, "Windows no pudo detener el Mobile Hotspot: " + result.AdditionalErrorMessage);
            var confirmed = await WaitForHotspotStateAsync(TetheringOperationalState.Off, TimeSpan.FromSeconds(15));
            return confirmed
                ? (true, "Windows confirma que el Mobile Hotspot está detenido.")
                : (false, "Windows aceptó la orden, pero no confirmó que el Mobile Hotspot se haya detenido.");
        }
        catch (Exception ex) { return (false, "Error al detener el Mobile Hotspot: " + ex.Message); }
        finally { _hotspotOperationInProgress = false; }
    }

    private Task<TetheringOperationalState?> GetModernHotspotStateAsync()
    {
        try
        {
            var profile = GetSelectedInternetConnectionProfile();
            if (profile is null) return Task.FromResult<TetheringOperationalState?>(null);
            return Task.FromResult<TetheringOperationalState?>(
                CreateTetheringManager(profile).TetheringOperationalState);
        }
        catch { return Task.FromResult<TetheringOperationalState?>(null); }
    }

    private async Task<bool> WaitForHotspotStateAsync(TetheringOperationalState expected, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        do
        {
            if (await GetModernHotspotStateAsync() == expected) return true;
            await Task.Delay(750);
        } while (DateTime.UtcNow < until);
        return false;
    }

    private async Task ConfirmHotspotStateAsync()
    {
        var confirmed = await WaitForHotspotStateAsync(TetheringOperationalState.On, TimeSpan.FromSeconds(25));
        HotspotStateText.Text = confirmed ? "Hotspot Activo" : "Hotspot Detenido";
        HotspotDetailText.Text = confirmed
            ? "Windows confirma que el Mobile Hotspot está activo."
            : "No se confirmó el estado activo del Mobile Hotspot. Comprueba la configuración de Windows.";
    }

    private async Task RefreshModernHotspotStateAsync()
    {
        if (_hotspotOperationInProgress) return;
        var state = await GetModernHotspotStateAsync();
        if (state == TetheringOperationalState.On)
        {
            HotspotStateText.Text = "Hotspot Activo";
            HotspotDetailText.Text = "Windows confirma que el Mobile Hotspot está activo.";
        }
        else if (state == TetheringOperationalState.Off)
        {
            HotspotStateText.Text = "Hotspot Detenido";
            HotspotDetailText.Text = "Windows confirma que el Mobile Hotspot está detenido.";
        }
    }

    private void AutoStartToggle_Checked(object sender, RoutedEventArgs e) => SetStartupPreference(true);
    private void AutoStartToggle_Unchecked(object sender, RoutedEventArgs e) => SetStartupPreference(false);

    private void SetStartupPreference(bool enabled)
    {
        if (_changingStartupToggle) return;
        var previous = _settings.StartWithWindows;
        try
        {
            ApplyStartupRegistration(enabled);
            _settings.StartWithWindows = enabled;
            SaveSettings();
        }
        catch (Exception ex)
        {
            _settings.StartWithWindows = previous;
            _changingStartupToggle = true;
            AutoStartToggle.IsChecked = previous;
            _changingStartupToggle = false;
            SetStatus("No se pudo " + (enabled ? "activar" : "desactivar") + " el inicio automático: " + ex.Message);
        }
    }

    private static void ApplyStartupRegistration(bool enabled)
    {
        var executable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (enabled && string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("No se pudo determinar la ruta del ejecutable.");

        // Remove the old Run-key entry. The app requestsAdministrator in its manifest,
        // so Windows Run launches it behind a UAC prompt and it cannot start unattended.
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
            key?.DeleteValue("MarkHotspot", throwOnMissingValue: false);

        if (enabled)
        {
            // A logon task configured with highest privileges lets the elevated app start
            // without displaying a UAC prompt. --background keeps the window in the tray.
            RunStartupTaskCommand("/Create", "/TN", "MarkHotspot", "/SC", "ONLOGON",
                "/TR", "\"" + executable + "\" --background", "/RL", "HIGHEST", "/F");
        }
        else
        {
            // schtasks returns an error when the task does not exist; query first so that
            // disabling autostart is still idempotent on a clean installation.
            if (RunStartupTaskCommand(new[] { "/Query", "/TN", "MarkHotspot" }, throwOnFailure: false) == 0)
                RunStartupTaskCommand("/Delete", "/TN", "MarkHotspot", "/F");
        }
    }

    private static int RunStartupTaskCommand(params string[] arguments) =>
        RunStartupTaskCommand(arguments, throwOnFailure: true);

    private static int RunStartupTaskCommand(string[] arguments, bool throwOnFailure)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("No se pudo iniciar el Programador de tareas de Windows.");
        if (!process.WaitForExit(15000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("El Programador de tareas de Windows tardó demasiado en responder.");
        }

        if (throwOnFailure && process.ExitCode != 0)
            throw new InvalidOperationException(
                "Windows no pudo configurar el inicio automático (schtasks, código " + process.ExitCode + ").");
        return process.ExitCode;
    }

    private void SetStatus(string message) => HotspotDetailText.Text = message;
    public sealed record AdapterRow(string Name, string Type, string Status, string Speed);
    public sealed class HotspotWifiAdapterRow
    {
        public HotspotWifiAdapterRow(string id, string displayName, string status, NetworkAdapter networkAdapter)
        { Id = id; DisplayName = displayName; Status = status; NetworkAdapter = networkAdapter; }
        public string Id { get; }
        public string DisplayName { get; }
        public string Status { get; }
        public NetworkAdapter NetworkAdapter { get; }
        public override string ToString() => DisplayName;
    }
    public sealed class DeviceRow : INotifyPropertyChanged
    {
        private string _ip;
        private string _name;
        private string _state;
        private string _trafficText = "↑ —   ↓ —";
        public DeviceRow(string ip, string mac, string state) { _ip = ip; Mac = mac; _name = SafeDeviceNameFromMac(mac); _state = state; }
        public string Ip { get => _ip; set { _ip = value; OnPropertyChanged(); } }
        public string Mac { get; }
        public string Name { get => _name; set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(DeviceIcon)); } }
        public string DeviceIcon => DetectDeviceIcon(Name);
        public string State { get => _state; set { _state = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayState)); } }
        public string DisplayState => MainWindow.IsBlockedDevice(Mac, Ip) ? "Bloqueado" : State;
        public void RefreshBlockedState() => OnPropertyChanged(nameof(DisplayState));
        public string TrafficText { get => _trafficText; set { _trafficText = value; OnPropertyChanged(); } }
        private long _totalUploadBytes;
        private long _totalDownloadBytes;
        public long TotalUploadBytes { get => _totalUploadBytes; set { _totalUploadBytes = Math.Max(0, value); OnPropertyChanged(); OnPropertyChanged(nameof(TotalTrafficText)); } }
        public long TotalDownloadBytes { get => _totalDownloadBytes; set { _totalDownloadBytes = Math.Max(0, value); OnPropertyChanged(); OnPropertyChanged(nameof(TotalTrafficText)); } }
        public string TotalTrafficText => $"↑ {TotalUploadBytes / 1048576d:0.00} MB   ↓ {TotalDownloadBytes / 1048576d:0.00} MB";
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    private static string DetectDeviceIcon(string name)
    {
        var n = name.ToLowerInvariant();
        if (new[] { "iphone", "ipad", "android", "galaxy", "samsung", "pixel", "huawei", "xiaomi", "redmi", "oppo", "phone", "mobile", "a17", "a16" }.Any(n.Contains)) return "📱";
        if (new[] { "windows", "desktop", "laptop", "computer", "macbook", "imac", "lenovo", "dell", "hp-", "asus", "acer", "surface" }.Any(n.Contains)) return "🖥";
        return "▣";
    }
    public sealed class TrafficLimitRow : INotifyPropertyChanged
    {
        private string _name, _ip, _state, _trafficText;
        private int _downloadMbps, _uploadMbps;
        public TrafficLimitRow(string name, string ip, string mac, int downloadMbps, int uploadMbps, string state, string trafficText)
        { _name=name; _ip=ip; Mac=mac; _downloadMbps=Math.Clamp(downloadMbps,1,100); _uploadMbps=Math.Clamp(uploadMbps,1,50); _state=state; _trafficText=trafficText; }
        public string Name { get=>_name; set { _name=value; OnPropertyChanged(); OnPropertyChanged(nameof(DeviceIcon)); } }
        public string Ip { get=>_ip; set { _ip=value; OnPropertyChanged(); } }
        public string Mac { get; }
        public string State { get=>_state; set { _state=value; OnPropertyChanged(); OnPropertyChanged(nameof(StateBrush)); } }
        public System.Windows.Media.Brush StateBrush => State.Equals("Conectado",StringComparison.OrdinalIgnoreCase) ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30,91,70)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(76,61,43));
        public string DeviceIcon => DetectDeviceIcon(Name);
        public int DownloadMbps { get=>_downloadMbps; set { _downloadMbps=Math.Clamp(value,1,100); OnPropertyChanged(); } }
        public int UploadMbps { get=>_uploadMbps; set { _uploadMbps=Math.Clamp(value,1,50); OnPropertyChanged(); } }
        public string TrafficText { get=>_trafficText; set { _trafficText=value; OnPropertyChanged(); } }
        private long _totalUploadBytes;
        private long _totalDownloadBytes;
        public long TotalUploadBytes { get=>_totalUploadBytes; set { _totalUploadBytes=Math.Max(0,value); OnPropertyChanged(); OnPropertyChanged(nameof(TotalTrafficText)); } }
        public long TotalDownloadBytes { get=>_totalDownloadBytes; set { _totalDownloadBytes=Math.Max(0,value); OnPropertyChanged(); OnPropertyChanged(nameof(TotalTrafficText)); } }
        public string TotalTrafficText => $"↑ {TotalUploadBytes / 1048576d:0.00} MB   ↓ {TotalDownloadBytes / 1048576d:0.00} MB";
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name=null) => PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));
    }

    private sealed class AppSettings
    {
        public bool IsDarkTheme { get; set; } = true;
        public bool StartWithWindows { get; set; } = true;
        public string Ssid { get; set; } = "MarkHotspot";
        public string Password { get; set; } = "ChangeMe123";
        public string SelectedHotspotAdapterId { get; set; } = "";
        public string SelectedInternetAdapterId { get; set; } = "";
        public List<BlockedDevice> BlockedDevices { get; set; } = new();
        public List<TrafficLimitSetting> TrafficLimits { get; set; } = new();
        public Dictionary<string, string> DeviceNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<DeviceCacheEntry> DeviceCache { get; set; } = new();
    }
    private sealed class DeviceCacheEntry
    {
        public string Mac { get; set; } = "";
        public string Ip { get; set; } = "";
        public string Name { get; set; } = "";
        public long TotalUploadBytes { get; set; }
        public long TotalDownloadBytes { get; set; }
        public string LastSeen { get; set; } = "";
    }
    private sealed class TrafficLimitSetting
    {
        public string Mac { get; set; } = "";
        public string Ip { get; set; } = "";
        public int DownloadMbps { get; set; } = 10;
        public int UploadMbps { get; set; } = 3;
        public int DownloadKBps { get; set; } = 1250;
        public int UploadKBps { get; set; } = 375;
        public string UpdatedAt { get; set; } = "";
    }
    private sealed class BlockedDevice
    {
        public string Ip { get; set; } = "";
        public string Mac { get; set; } = "";
        public string AddedAt { get; set; } = "";
    }
}