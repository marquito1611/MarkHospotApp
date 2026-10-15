using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using System.Windows;

namespace MarkHotspot;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<AdapterRow> _adapters = new();

    public MainWindow()
    {
        InitializeComponent();
        AdaptersList.ItemsSource = _adapters;
        RefreshAdapters();
        NetworkChange.NetworkAddressChanged += NetworkChange_NetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += NetworkChange_NetworkAvailabilityChanged;
        Closed += (_, _) =>
        {
            NetworkChange.NetworkAddressChanged -= NetworkChange_NetworkAddressChanged;
            NetworkChange.NetworkAvailabilityChanged -= NetworkChange_NetworkAvailabilityChanged;
        };
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshAdapters();

    private void NetworkChange_NetworkAddressChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(RefreshAdapters);

    private void NetworkChange_NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        Dispatcher.BeginInvoke(RefreshAdapters);

    private void RefreshAdapters()
    {
        try
        {
            var rows = NetworkInterface.GetAllNetworkInterfaces()
                .Select(n => new AdapterRow(
                    n.Name,
                    n.NetworkInterfaceType.ToString(),
                    n.OperationalStatus.ToString(),
                    n.Speed > 0 ? $"{n.Speed / 1_000_000d:0.#} Mbps" : "—"))
                .OrderByDescending(n => n.Status == "Up")
                .ThenBy(n => n.Name)
                .ToList();

            _adapters.Clear();
            foreach (var row in rows) _adapters.Add(row);
            AdapterCountText.Text = rows.Count.ToString();
            HotspotStateText.Text = "No verificado";
            HotspotDetailText.Text = "Se detectaron las interfaces de Windows. El estado del Mobile Hotspot requiere integración con la API de tethering y aún no se consulta aquí.";
        }
        catch (Exception ex)
        {
            HotspotStateText.Text = "Error de diagnóstico";
            HotspotDetailText.Text = ex.Message;
        }
    }

    public sealed record AdapterRow(string Name, string Type, string Status, string Speed);
}
