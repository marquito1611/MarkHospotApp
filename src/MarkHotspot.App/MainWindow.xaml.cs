using System.IO;
using System.Diagnostics;
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

namespace MarkHotspot;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<AdapterRow> _adapters = new();
    private readonly ObservableCollection<DeviceRow> _devices = new();
    private readonly Dictionary<string, DeviceRow> _devicesByMac = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _macByIp = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TrafficCounter> _trafficByMac = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _trafficTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ICaptureDevice? _captureDevice;
    private bool _captureStarted;
    private readonly ObservableCollection<BlockedDeviceRow> _blockedDevices = new();
    private readonly string _dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkHotspot");
    private readonly string _settingsPath;
    private AppSettings _settings = new();
    private bool _changingTheme;

    public MainWindow()
    {
        InitializeComponent();
        _settingsPath = Path.Combine(_dataDirectory, "settings.json");
        AdaptersList.ItemsSource = _adapters;
        DevicesList.ItemsSource = _devices;
        _trafficTimer.Tick += (_, _) => UpdateTrafficRates();
        _trafficTimer.Start();
        BlacklistList.ItemsSource = _blockedDevices;
        LoadSettings();
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
        LoadBlacklistRows();
        NetworkChange.NetworkAddressChanged += NetworkChange_NetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += NetworkChange_NetworkAvailabilityChanged;
        Closed += (_, _) =>
        {
            NetworkChange.NetworkAddressChanged -= NetworkChange_NetworkAddressChanged;
            NetworkChange.NetworkAvailabilityChanged -= NetworkChange_NetworkAvailabilityChanged;
            _trafficTimer.Stop();
            StopTrafficCapture();
        };
    }

    private void ShowPage(System.Windows.Controls.StackPanel page)
    {
        HomePage.Visibility = Visibility.Collapsed;
        DevicesPage.Visibility = Visibility.Collapsed;
        TrafficPage.Visibility = Visibility.Collapsed;
        BlacklistPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;

        var active = page == HomePage ? HomeNavButton
            : page == DevicesPage ? DevicesNavButton
            : page == TrafficPage ? TrafficNavButton
            : page == BlacklistPage ? BlacklistNavButton
            : SettingsNavButton;

        var selectedBackground = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                _settings.IsDarkTheme ? "#2A3B59" : "#DBEAFE"));
        var activeForeground = (System.Windows.Media.Brush)Application.Current.Resources["MainTextBrush"];
        var inactiveForeground = (System.Windows.Media.Brush)Application.Current.Resources["MutedBrush"];

        foreach (var button in new[] { HomeNavButton, DevicesNavButton, TrafficNavButton, BlacklistNavButton, SettingsNavButton })
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
        if (Application.Current.Resources[key] is System.Windows.Media.SolidColorBrush brush)
            brush.Color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
    }

    private void DevicesNav_Click(object sender, RoutedEventArgs e) => ShowPage(DevicesPage);
    private void TrafficNav_Click(object sender, RoutedEventArgs e) => ShowPage(TrafficPage);
    private void BlacklistNav_Click(object sender, RoutedEventArgs e) => ShowPage(BlacklistPage);
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => ShowPage(SettingsPage);

    private void TogglePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (PasswordBox.Visibility == Visibility.Visible)
        {
            PasswordVisibleBox.Text = PasswordBox.Password;
            PasswordBox.Visibility = Visibility.Collapsed;
            PasswordVisibleBox.Visibility = Visibility.Visible;
            PasswordVisibleBox.Focus();
            PasswordVisibleBox.CaretIndex = PasswordVisibleBox.Text.Length;
        }
        else
        {
            PasswordBox.Password = PasswordVisibleBox.Text;
            PasswordVisibleBox.Visibility = Visibility.Collapsed;
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
    private async void RemoveFromBlacklistButton_Click(object sender, RoutedEventArgs e) => await RemoveSelectedDeviceFromBlacklistAsync();

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
            SetStatus("Configurando Hosted Network heredado de Windows…");
            var config = await RunNetshAsync($"wlan set hostednetwork mode=allow ssid=\"{ssid}\" key=\"{password}\" keyUsage=persistent");
            if (config.ExitCode != 0 || config.Output.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
                config.Output.Contains("no se puede", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Windows rechazó Hosted Network. Muchos controladores modernos no admiten esta API heredada. " + Short(config.Output));
                return;
            }
            var start = await RunNetshAsync("wlan start hostednetwork");
            SetStatus(start.ExitCode == 0 ? "Windows aceptó la orden de inicio. La detección de clientes depende de la tabla de vecinos y puede no mostrar todos los dispositivos." : "No se pudo iniciar el punto de acceso: " + Short(start.Output));
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
            var result = await RunNetshAsync("wlan stop hostednetwork");
            SetStatus(result.ExitCode == 0 ? "Windows aceptó la orden para detener Hosted Network." : "No se pudo detener Hosted Network: " + Short(result.Output));
        }
        catch (Exception ex) { SetStatus("Error al detener: " + ex.Message); }
        finally { StopButton.IsEnabled = true; }
    }

    private async Task ScanDevicesAsync()
    {
        ScanDevicesButton.IsEnabled = false;
        try
        {
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
                if (_settings.Blacklist.Any(b => !string.IsNullOrWhiteSpace(b.Mac) && b.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase)))
                    row.State += " · Lista negra";
            }

            foreach (var row in _devices)
                if (!found.ContainsKey(row.Mac) && !connectedMacs.Contains(row.Mac))
                    row.State = "Desconectado" + (_settings.Blacklist.Any(b => b.Mac.Equals(row.Mac, StringComparison.OrdinalIgnoreCase)) ? " · Lista negra" : "");

            var ordered = _devices.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
            _devices.Clear();
            foreach (var row in ordered) _devices.Add(row);
            StartTrafficCapture();
            DevicesCountText.Text = _devices.Count.ToString();
            SetStatus(_captureStarted ? $"Lista actualizada: {_devices.Count} dispositivos únicos por MAC. Tráfico en vivo activo." : $"Lista actualizada: {_devices.Count} dispositivos únicos por MAC. Para medir tráfico instala Npcap (modo compatible con WinPcap) y vuelve a buscar dispositivos.");
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
        return "Dispositivo " + mac[^5..].Replace(":", "");
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

    private async Task AddSelectedDeviceToBlacklistAsync()
    {
        if (DevicesList.SelectedItem is not DeviceRow selected) { SetStatus("Selecciona primero un dispositivo detectado."); return; }
        if (!IPAddress.TryParse(selected.Ip, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        { SetStatus("La dirección IP seleccionada no es válida."); return; }
        if (_settings.Blacklist.Any(b => b.Ip == selected.Ip)) { SetStatus("Ese dispositivo ya está en la lista negra."); return; }

        var entry = new BlockedDevice { Ip = selected.Ip, Mac = selected.Mac, AddedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm") };
        _settings.Blacklist.Add(entry);
        try { SaveSettings(); }
        catch (Exception ex) { _settings.Blacklist.Remove(entry); SetStatus("No se pudo guardar la lista negra: " + ex.Message); return; }

        var safeName = "MarkHotspot-Block-" + selected.Ip.Replace('.', '-');
        var inbound = await RunNetshAsync($"advfirewall firewall add rule name=\"{safeName}-In\" dir=in action=block remoteip={selected.Ip} enable=yes");
        var outbound = await RunNetshAsync($"advfirewall firewall add rule name=\"{safeName}-Out\" dir=out action=block remoteip={selected.Ip} enable=yes");
        LoadBlacklistRows();
        SetStatus(inbound.ExitCode == 0 && outbound.ExitCode == 0
            ? $"Añadido {selected.Ip} a la lista negra y creadas reglas de Firewall locales. Importante: estas reglas no garantizan bloquear tráfico reenviado del hotspot."
            : $"Guardado {selected.Ip} en la lista negra, pero falló alguna regla de Firewall. Ejecuta como administrador. {Short(inbound.Output + " " + outbound.Output)}");
        await ScanDevicesAsync();
    }

    private async Task RemoveSelectedDeviceFromBlacklistAsync()
    {
        if (BlacklistList.SelectedItem is not BlockedDeviceRow selected) { SetStatus("Selecciona un dispositivo de la lista negra."); return; }
        _settings.Blacklist.RemoveAll(b => b.Ip == selected.Ip);
        try { SaveSettings(); }
        catch (Exception ex) { SetStatus("No se pudo guardar el cambio: " + ex.Message); return; }
        var safeName = "MarkHotspot-Block-" + selected.Ip.Replace('.', '-');
        await RunNetshAsync($"advfirewall firewall delete rule name=\"{safeName}-In\"");
        await RunNetshAsync($"advfirewall firewall delete rule name=\"{safeName}-Out\"");
        LoadBlacklistRows();
        SetStatus($"Quitado {selected.Ip} de la lista negra y eliminadas las reglas locales asociadas.");
    }

    private void LoadBlacklistRows()
    {
        _blockedDevices.Clear();
        foreach (var b in _settings.Blacklist.OrderBy(b => b.Ip))
            _blockedDevices.Add(new BlockedDeviceRow(b.Ip, string.IsNullOrWhiteSpace(b.Mac) ? "MAC desconocida" : b.Mac, b.AddedAt));
        if (BlacklistCountText is not null) BlacklistCountText.Text = _blockedDevices.Count.ToString();
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
            HotspotStateText.Text = "Estado no confirmado";
            HotspotDetailText.Text = "La lista de interfaces es real. El estado del Mobile Hotspot moderno no se confirma con esta API.";
        }
        catch (Exception ex) { SetStatus("Error de diagnóstico de red: " + ex.Message); }
        return Task.CompletedTask;
    }

    private void SetStatus(string message) => HotspotDetailText.Text = message;
    public sealed record AdapterRow(string Name, string Type, string Status, string Speed);
    public sealed class DeviceRow : INotifyPropertyChanged
    {
        private string _ip;
        private string _name;
        private string _state;
        private string _trafficText = "↑ —   ↓ —";
        public DeviceRow(string ip, string mac, string state) { _ip = ip; Mac = mac; _name = "Dispositivo " + mac[^5..].Replace(":", ""); _state = state; }
        public string Ip { get => _ip; set { _ip = value; OnPropertyChanged(); } }
        public string Mac { get; }
        public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
        public string DeviceIcon => DetectDeviceIcon(Name);
        public string State { get => _state; set { _state = value; OnPropertyChanged(); } }
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
    public sealed record BlockedDeviceRow(string Ip, string Mac, string AddedAt);
    private sealed class AppSettings
    {
        public bool IsDarkTheme { get; set; } = true;
        public string Ssid { get; set; } = "MarkHotspot";
        public string Password { get; set; } = "ChangeMe123";
        public List<BlockedDevice> Blacklist { get; set; } = new();
    }
    private sealed class BlockedDevice
    {
        public string Ip { get; set; } = "";
        public string Mac { get; set; } = "";
        public string AddedAt { get; set; } = "";
    }
}