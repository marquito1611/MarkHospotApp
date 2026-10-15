using System.IO;
using System.Diagnostics;
using Microsoft.Win32;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
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
    private readonly ObservableCollection<DeviceRow> _devices = new();
    private readonly ObservableCollection<TrafficLimitRow> _trafficLimits = new();
    private readonly Dictionary<string, DeviceRow> _devicesByMac = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _macByIp = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TrafficCounter> _trafficByMac = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _trafficTimer = new() { Interval = TimeSpan.FromSeconds(1) };
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
    private bool _hotspotOperationInProgress;

    public MainWindow()
    {
        InitializeComponent();
        InitializeTrayIcon();
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        _settingsPath = Path.Combine(_dataDirectory, "settings.json");
        AdaptersList.ItemsSource = _adapters;
        DevicesList.ItemsSource = _devices;
        TrafficLimitsList.ItemsSource = _trafficLimits;
        _trafficTimer.Tick += (_, _) => UpdateTrafficRates();
        _trafficTimer.Start();
        LoadSettings();
        CurrentSettings = _settings;
        AutoStartToggle.IsChecked = _settings.StartWithWindows;
        ApplyStartupRegistration(_settings.StartWithWindows);
        Loaded += async (_, _) => await StartModernHotspotOnLaunchAsync();
        _hotspotStateTimer.Tick += async (_, _) => await RefreshModernHotspotStateAsync();
        _hotspotStateTimer.Start();
        LoadTrafficLimitSettings();
        _changingTheme = true;
        DarkThemeToggle.IsChecked = _settings.IsDarkTheme;
        LightThemeToggle.IsChecked = !_settings.IsDarkTheme;
        _changingTheme = false;
        ApplyTheme(_settings.IsDarkTheme);
        SsidBox.Text = _settings.Ssid;
        SettingsSsidBox.Text = _settings.Ssid;
        PasswordBox.Password = _settings.Password;
        PasswordVisibleBox.Text = _settings.Password;
        RefreshAdapters();
        NetworkChange.NetworkAddressChanged += NetworkChange_NetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += NetworkChange_NetworkAvailabilityChanged;
        Closed += (_, _) =>
        {
            NetworkChange.NetworkAddressChanged -= NetworkChange_NetworkAddressChanged;
            NetworkChange.NetworkAvailabilityChanged -= NetworkChange_NetworkAvailabilityChanged;
            _trafficTimer.Stop();
            _hotspotStateTimer.Stop();
            StopTrafficCapture();
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        };
    }

    private void InitializeTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Abrir MarkHotspot", null, (_, _) => ShowFromTray());
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
        Show();
        WindowState = WindowState.Normal;
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
        if (dark) LightThemeToggle.IsChecked = false;
        else DarkThemeToggle.IsChecked = false;
        _settings.IsDarkTheme = dark;
        ApplyTheme(dark);
        try { SaveSettings(); }
        catch (Exception ex) { SetStatus("No se pudo guardar el tema: " + ex.Message); }
        _changingTheme = false;
    }

    private static void ApplyTheme(bool dark)
    {
        SetBrushColor("BackgroundBrush", dark ? "#101319" : "#F3F6FB");
        SetBrushColor("PanelBrush", dark ? "#191E27" : "#FFFFFF");
        SetBrushColor("AccentBrush", dark ? "#3B82F6" : "#2563EB");
        SetBrushColor("MutedBrush", dark ? "#9AA4B2" : "#5B6472");
        SetBrushColor("MainTextBrush", dark ? "#F4F6FA" : "#172033");
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

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var ssid = SettingsSsidBox.Text.Trim();
        if (ssid.Length is < 1 or > 32)
        {
            SetStatus("El nombre Wi-Fi debe tener entre 1 y 32 caracteres.");
            return;
        }
        _settings.Ssid = ssid;
        SsidBox.Text = ssid;
        try
        {
            SaveSettings();
            SetStatus("Nombre de red guardado. Para aplicarlo al hotspot, pulsa Iniciar hotspot heredado.");
        }
        catch (Exception ex) { SetStatus("No se pudo guardar la configuración: " + ex.Message); }
    }

    private void LoadSettings()
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            if (File.Exists(_settingsPath))
                _settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath)) ?? new();
            _settings.Blacklist ??= new();
            _settings.TrafficLimits ??= new();
        }
        catch (Exception ex) { SetStatus("No se pudo leer la configuración: " + ex.Message); }
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
    private async void AddToBlacklistButton_Click(object sender, RoutedEventArgs e) => await AddSelectedDeviceToBlacklistAsync();

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
            _settings.Ssid = ssid;
            _settings.Password = password;
            SaveSettings();
            HotspotStateText.Text = "Activando Hotspot…";
            HotspotDetailText.Text = "Solicitando a Windows que active el Mobile Hotspot moderno…";
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
        ScanDevicesButton.IsEnabled = false;
        try
        {
            if (!IsNpcapInstalled())
            {
                var installed = await DownloadAndInstallNpcapAsync();
                if (!installed)
                {
                    SetStatus("No se pudo confirmar la instalación de Npcap. Instálalo y vuelve a buscar dispositivos.");
                    return;
                }
            }

            SetStatus("Buscando dispositivos por MAC y resolviendo sus nombres…");
            var hotspotAddresses = GetHotspotInterfaceAddresses().ToList();
            if (hotspotAddresses.Count == 0)
            {
                DevicesCountText.Text = _devices.Count.ToString();
                SetStatus("No se encontró la interfaz local del hotspot. Inicia el hotspot de Windows y vuelve a buscar.");
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
                    row = new DeviceRow(device.Ip, mac, "Desconectado") { Name = name };
                    _devicesByMac[mac] = row;
                    _devices.Add(row);
                }
                row.Ip = device.Ip;
                var ping = await PingDeviceAsync(device.Ip);
                row.State = connectedMacs.Contains(mac) || ping ? "Conectado" : "Desconectado";
            }

            foreach (var row in _devices)
                if (!found.ContainsKey(row.Mac) && !connectedMacs.Contains(row.Mac))
                    row.State = "Desconectado";

            var ordered = _devices.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
            _devices.Clear();
            foreach (var row in ordered) _devices.Add(row);
            SyncTrafficLimitRows();
            StartTrafficCapture();
            DevicesCountText.Text = _devices.Count.ToString();
            SetStatus(_captureStarted ? $"Lista actualizada: {_devices.Count} dispositivos únicos por MAC. Tráfico en vivo activo." : $"Lista actualizada: {_devices.Count} dispositivos únicos por MAC. Npcap está instalado, pero no se pudo iniciar la captura en la interfaz del hotspot.");
        }
        catch (Exception ex) { SetStatus("Error al buscar clientes del hotspot: " + ex.Message); }
        finally { ScanDevicesButton.IsEnabled = true; }
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
                d.Name.Contains(n.Id.Trim('{', '}'), StringComparison.OrdinalIgnoreCase) ||
                d.Description.Contains(n.Description, StringComparison.OrdinalIgnoreCase) ||
                n.Description.Contains(d.Description, StringComparison.OrdinalIgnoreCase)));
            if (candidate is null) return;
            candidate.OnPacketArrival += TrafficPacketArrived;
            candidate.Open(DeviceModes.Promiscuous, 1000);
            candidate.StartCapture();
            _captureDevice = candidate;
            _captureStarted = true;
        }
        catch { StopTrafficCapture(); }
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
                row.TrafficText = $"↑ {FormatRate(counter.TakeUpload())}   ↓ {FormatRate(counter.TakeDownload())}";
            else
                row.TrafficText = _captureStarted ? "↑ 0 B/s   ↓ 0 B/s" : "↑ —   ↓ —";

            var limitRow = _trafficLimits.FirstOrDefault(x => x.Mac.Equals(row.Mac, StringComparison.OrdinalIgnoreCase));
            if (limitRow is not null)
                limitRow.TrafficText = row.TrafficText;
        }
    }

    private static string FormatRate(long bytesPerSecond) =>
        bytesPerSecond >= 1024 * 1024 ? $"{bytesPerSecond / 1024d / 1024d:0.0} MB/s" :
        bytesPerSecond >= 1024 ? $"{bytesPerSecond / 1024d:0.0} KB/s" : $"{bytesPerSecond} B/s";

    private sealed class TrafficCounter
    {
        private long _upload;
        private long _download;
        public void AddUpload(long bytes) => Interlocked.Add(ref _upload, bytes);
        public void AddDownload(long bytes) => Interlocked.Add(ref _download, bytes);
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
            var looksLikeHotspot = name.Contains("Local Area Connection*", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase)
                || description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase)
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
            row.State = device.State;
            row.TrafficText = device.TrafficText;
            _trafficLimits.Add(row);
        }
        if (TrafficEngineStatusText is not null)
            TrafficEngineStatusText.Text = "Las reglas por MAC se pueden configurar y guardar. El motor que regula paquetes reenviados por Windows todavía no está activo; no se aplican límites reales aún.";
    }

    private void ApplyDeviceLimit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: TrafficLimitRow row }) return;
        if (!IPAddress.TryParse(row.Ip, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            SetStatus("No se puede guardar la regla: el dispositivo todavía no tiene una IP IPv4 válida. Busca los dispositivos e inténtalo de nuevo.");
            return;
        }
        _settings.TrafficLimits.RemoveAll(x => x.Mac.Equals(row.Mac, StringComparison.OrdinalIgnoreCase));
        _settings.TrafficLimits.Add(new TrafficLimitSetting { Mac = row.Mac.ToUpperInvariant(), Ip = row.Ip, DownloadMbps = row.DownloadMbps, UploadMbps = row.UploadMbps, UpdatedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss") });
        try
        {
            SaveSettings();
            SetStatus($"Límites guardados para {row.Name} ({row.Mac}): descarga {row.DownloadMbps} Mbps y subida {row.UploadMbps} Mbps. Atención: todavía no se aplican al tráfico real.");
            TrafficEngineStatusText.Text = "Configuración guardada localmente. Falta integrar y probar el motor de shaping de paquetes reenviados para aplicar límites reales.";
        }
        catch (Exception ex) { SetStatus("No se pudieron guardar los límites: " + ex.Message); }
    }

    private static AppSettings? CurrentSettings;
    private static bool IsBlockedDevice(string mac, string ip) =>
        CurrentSettings?.Blacklist.Any(b =>
            (!string.IsNullOrWhiteSpace(mac) && b.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(ip) && b.Ip.Equals(ip, StringComparison.OrdinalIgnoreCase))) == true;

    private void DevicesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateBlockButton();
    private void UpdateBlockButton()
    {
        if (AddToBlacklistButton is null) return;
        var selected = DevicesList.SelectedItem as DeviceRow;
        AddToBlacklistButton.IsEnabled = selected is not null;
        AddToBlacklistButton.Content = selected is not null && IsBlockedDevice(selected.Mac, selected.Ip) ? "Desbloquear dispositivo" : "Bloquear dispositivo";
    }

    private async Task AddSelectedDeviceToBlacklistAsync()
    {
        if (DevicesList.SelectedItem is not DeviceRow selected) { SetStatus("Selecciona primero un dispositivo detectado."); return; }
        var safeName = "MarkHotspot-Block-" + selected.Ip.Replace('.', '-');
        if (IsBlockedDevice(selected.Mac, selected.Ip))
        {
            var previous = _settings.Blacklist.Where(b => b.Ip.Equals(selected.Ip, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(selected.Mac) && b.Mac.Equals(selected.Mac, StringComparison.OrdinalIgnoreCase))).ToList();
            _settings.Blacklist.RemoveAll(b => b.Ip.Equals(selected.Ip, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(selected.Mac) && b.Mac.Equals(selected.Mac, StringComparison.OrdinalIgnoreCase)));
            try { SaveSettings(); }
            catch (Exception ex) { _settings.Blacklist.AddRange(previous); SetStatus("No se pudo guardar el desbloqueo: " + ex.Message); return; }
            await RunNetshAsync($"advfirewall firewall delete rule name=\"{safeName}-In\"");
            await RunNetshAsync($"advfirewall firewall delete rule name=\"{safeName}-Out\"");
            foreach (var row in _devices) row.RefreshBlockedState();
            UpdateBlockButton();
            SetStatus($"Dispositivo {selected.Ip} desbloqueado. Las reglas locales de Firewall no garantizan filtrar el tráfico reenviado del hotspot.");
            return;
        }
        if (!IPAddress.TryParse(selected.Ip, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        { SetStatus("La dirección IP seleccionada no es válida."); return; }
        var entry = new BlockedDevice { Ip = selected.Ip, Mac = selected.Mac, AddedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm") };
        _settings.Blacklist.Add(entry);
        try { SaveSettings(); }
        catch (Exception ex) { _settings.Blacklist.Remove(entry); SetStatus("No se pudo guardar el bloqueo: " + ex.Message); return; }
        var inbound = await RunNetshAsync($"advfirewall firewall add rule name=\"{safeName}-In\" dir=in action=block remoteip={selected.Ip} enable=yes");
        var outbound = await RunNetshAsync($"advfirewall firewall add rule name=\"{safeName}-Out\" dir=out action=block remoteip={selected.Ip} enable=yes");
        foreach (var row in _devices) row.RefreshBlockedState();
        UpdateBlockButton();
        SetStatus(inbound.ExitCode == 0 && outbound.ExitCode == 0
            ? $"Dispositivo {selected.Ip} marcado como bloqueado. Las reglas locales no garantizan filtrar el tráfico reenviado del hotspot."
            : $"Se guardó el bloqueo de {selected.Ip}, pero falló alguna regla de Firewall. Ejecuta como administrador.");
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
        Dispatcher.BeginInvoke(new Action(() => _ = RefreshAdaptersAsync()));
    private void NetworkChange_NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => _ = RefreshAdaptersAsync()));
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
            AdapterCountText.Text = rows.Count.ToString();
        }
        catch (Exception ex) { SetStatus("Error de diagnóstico de red: " + ex.Message); }
        return Task.CompletedTask;
    }

    private async Task StartModernHotspotOnLaunchAsync()
    {
        HotspotStateText.Text = "Activando Hotspot…";
        HotspotDetailText.Text = "Comprobando el estado del Mobile Hotspot de Windows…";
        var state = await GetModernHotspotStateAsync();
        if (state == TetheringOperationalState.On)
        {
            HotspotStateText.Text = "Hotspot Activo";
            HotspotDetailText.Text = "Windows confirma que el Mobile Hotspot está activo.";
            return;
        }

        HotspotStateText.Text = "Activando Hotspot…";
        var result = await StartModernHotspotAsync();
        HotspotDetailText.Text = result.Message;
        if (result.Success) await ConfirmHotspotStateAsync();
        else HotspotStateText.Text = "Hotspot Detenido";
    }

    private async Task<(bool Success, string Message)> StartModernHotspotAsync()
    {
        if (_hotspotOperationInProgress) return (false, "Ya hay una operación del hotspot en curso.");
        _hotspotOperationInProgress = true;
        try
        {
            HotspotStateText.Text = "Activando Hotspot…";
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null)
                return (false, "Windows no tiene un perfil de conexión a Internet disponible. Conecta primero la PC a Internet.");

            var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
            if (manager.TetheringOperationalState == TetheringOperationalState.On)
                return (true, "Windows confirma que el Mobile Hotspot está activo.");

            var result = await manager.StartTetheringAsync();
            if (result.Status != TetheringOperationStatus.Success)
                return (false, "Windows no pudo activar el Mobile Hotspot: " + result.AdditionalErrorMessage);

            var confirmed = await WaitForHotspotStateAsync(TetheringOperationalState.On, TimeSpan.FromSeconds(25));
            return confirmed
                ? (true, "Windows confirma que el Mobile Hotspot está activo.")
                : (false, "Windows aceptó la solicitud, pero no confirmó que el Mobile Hotspot esté activo.");
        }
        catch (Exception ex)
        {
            return (false, "No se pudo consultar o activar el Mobile Hotspot moderno de Windows: " + ex.Message);
        }
        finally { _hotspotOperationInProgress = false; }
    }

    private async Task<(bool Success, string Message)> StopModernHotspotAsync()
    {
        if (_hotspotOperationInProgress) return (false, "Ya hay una operación del hotspot en curso.");
        _hotspotOperationInProgress = true;
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) return (false, "Windows no expone un perfil de conexión para controlar el Mobile Hotspot.");
            var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
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
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) return Task.FromResult<TetheringOperationalState?>(null);
            return Task.FromResult<TetheringOperationalState?>(
                NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile).TetheringOperationalState);
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

    private void AutoStartToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.StartWithWindows = true;
        try { ApplyStartupRegistration(true); SaveSettings(); }
        catch (Exception ex) { SetStatus("No se pudo activar el inicio automático: " + ex.Message); }
    }

    private void AutoStartToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.StartWithWindows = false;
        try { ApplyStartupRegistration(false); SaveSettings(); }
        catch (Exception ex) { SetStatus("No se pudo desactivar el inicio automático: " + ex.Message); }
    }

    private static void ApplyStartupRegistration(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
            ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("No se pudo determinar la ruta del ejecutable.");
            key.SetValue("MarkHotspot", "\"" + executable + "\"");
        }
        else key.DeleteValue("MarkHotspot", throwOnMissingValue: false);
    }

    private void SetStatus(string message) => HotspotDetailText.Text = message;
    public sealed record AdapterRow(string Name, string Type, string Status, string Speed);
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
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name=null) => PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));
    }

    public sealed record BlockedDeviceRow(string Ip, string Mac, string AddedAt);
    private sealed class AppSettings
    {
        public bool IsDarkTheme { get; set; } = true;
        public bool StartWithWindows { get; set; } = true;
        public string Ssid { get; set; } = "MarkHotspot";
        public string Password { get; set; } = "ChangeMe123";
        public List<BlockedDevice> Blacklist { get; set; } = new();
        public List<TrafficLimitSetting> TrafficLimits { get; set; } = new();
    }
    private sealed class TrafficLimitSetting
    {
        public string Mac { get; set; } = "";
        public string Ip { get; set; } = "";
        public int DownloadMbps { get; set; } = 10;
        public int UploadMbps { get; set; } = 3;
        public string UpdatedAt { get; set; } = "";
    }
    private sealed class BlockedDevice
    {
        public string Ip { get; set; } = "";
        public string Mac { get; set; } = "";
        public string AddedAt { get; set; } = "";
    }
}