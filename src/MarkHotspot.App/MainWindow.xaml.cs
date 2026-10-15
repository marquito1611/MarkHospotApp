using System.IO;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace MarkHotspot;

public partial class MainWindow : Window
{
    private readonly System.Collections.ObjectModel.ObservableCollection<AdapterRow> _adapters = new();
    private readonly string _dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkHotspot");
    private readonly string _settingsPath;
    private AppSettings _settings = new();

    public MainWindow()
    {
        InitializeComponent();
        _settingsPath = Path.Combine(_dataDirectory, "settings.json");
        AdaptersList.ItemsSource = _adapters;
        LoadSettings();
        SsidBox.Text = _settings.Ssid;
        PasswordBox.Password = _settings.Password;
        RefreshAdapters();
        NetworkChange.NetworkAddressChanged += NetworkChange_NetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += NetworkChange_NetworkAvailabilityChanged;
        Closed += (_, _) =>
        {
            NetworkChange.NetworkAddressChanged -= NetworkChange_NetworkAddressChanged;
            NetworkChange.NetworkAvailabilityChanged -= NetworkChange_NetworkAvailabilityChanged;
        };
    }

    private void LoadSettings()
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            if (File.Exists(_settingsPath))
                _settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath)) ?? new();
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

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        try
        {
            var ssid = SsidBox.Text.Trim();
            var password = PasswordBox.Password;
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
            SetStatus(start.ExitCode == 0 ? "Windows aceptó la orden de inicio. Comprueba que la red esté anunciándose; ICS puede requerir configuración aparte." : "No se pudo iniciar el punto de acceso: " + Short(start.Output));
            await RefreshAdaptersAsync();
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

    private async Task<(int ExitCode, string Output)> RunNetshAsync(string arguments)
    {
        var psi = new ProcessStartInfo("netsh.exe", arguments)
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
            HotspotDetailText.Text = "La lista de interfaces es real. Windows no confirma aquí el estado del Mobile Hotspot moderno.";
        }
        catch (Exception ex) { SetStatus("Error de diagnóstico de red: " + ex.Message); }
        return Task.CompletedTask;
    }

    private void SetStatus(string message) => HotspotDetailText.Text = message;
    public sealed record AdapterRow(string Name, string Type, string Status, string Speed);
    private sealed class AppSettings
    {
        public string Ssid { get; set; } = "MarkHotspot";
        public string Password { get; set; } = "ChangeMe123";
    }
}
