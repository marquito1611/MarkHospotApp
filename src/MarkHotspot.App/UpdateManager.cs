using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace MarkHotspot;

public partial class MainWindow
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/marquito1611/MarkHospotApp/releases?per_page=50";
    private static readonly HttpClient UpdateHttpClient = CreateUpdateHttpClient();
    private CancellationTokenSource? _updateCancellation;
    private AvailableUpdate? _availableUpdate;

    private sealed record AvailableUpdate(string Tag, string DownloadUrl, long Size, string Notes);

    private static HttpClient CreateUpdateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MarkHotspot-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static Version GetInstalledAssemblyVersion() =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0, 0);

    private static Version GetComparableInstalledVersion()
    {
        var version = GetInstalledAssemblyVersion();
        return new Version(Math.Max(0, version.Major), Math.Max(0, version.Minor), Math.Max(0, version.Build));
    }

    private static string GetInstalledVersionLabel()
    {
        var version = GetComparableInstalledVersion();
        return $"v{version.Major}.{version.Minor}.{version.Build}-Public_Release";
    }

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateCancellation is not null)
        {
            _updateCancellation.Cancel();
            return;
        }

        if (_availableUpdate is not null)
        {
            await DownloadAndInstallUpdateAsync(_availableUpdate);
            return;
        }

        await CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        CheckForUpdatesButton.IsEnabled = false;
        _availableUpdate = null;
        UpdateProgressPanel.Visibility = Visibility.Collapsed;
        UpdateReleaseInfoText.Text = string.Empty;
        UpdateStatusText.Text = "Buscando la última versión estable en GitHub…";
        try
        {
            using var apiResponse = await UpdateHttpClient.GetAsync(LatestReleaseApiUrl, HttpCompletionOption.ResponseHeadersRead);
            apiResponse.EnsureSuccessStatusCode();
            using var releaseDocument = JsonDocument.Parse(await apiResponse.Content.ReadAsStringAsync());
            var releases = releaseDocument.RootElement;
            if (releases.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("GitHub devolvió una lista de publicaciones con formato no válido.");

            // Do not trust /releases/latest alone: if a malformed or draft release is
            // marked latest, it can hide valid stable releases. Pick the highest valid
            // stable Public_Release tag from the release list instead.
            JsonElement release = default;
            string? tag = null;
            var latestVersion = new Version(0, 0, 0);
            foreach (var candidate in releases.EnumerateArray())
            {
                var candidateTag = candidate.TryGetProperty("tag_name", out var candidateTagElement)
                    ? candidateTagElement.GetString()?.Trim()
                    : null;
                if (string.IsNullOrWhiteSpace(candidateTag))
                    continue;

                var versionMatch = Regex.Match(candidateTag, @"^v(\d+)\.(\d+)\.(\d+)-Public_Release$", RegexOptions.IgnoreCase);
                if (!versionMatch.Success ||
                    !int.TryParse(versionMatch.Groups[1].Value, out var candidateMajor) ||
                    !int.TryParse(versionMatch.Groups[2].Value, out var candidateMinor) ||
                    !int.TryParse(versionMatch.Groups[3].Value, out var candidatePatch))
                    continue;

                if ((candidate.TryGetProperty("draft", out var candidateDraft) && candidateDraft.GetBoolean()) ||
                    (candidate.TryGetProperty("prerelease", out var candidatePrerelease) && candidatePrerelease.GetBoolean()))
                    continue;

                var candidateVersion = new Version(candidateMajor, candidateMinor, candidatePatch);
                if (candidateVersion <= latestVersion)
                    continue;

                release = candidate;
                tag = candidateTag;
                latestVersion = candidateVersion;
            }

            if (release.ValueKind != JsonValueKind.Object || tag is null)
                throw new InvalidDataException("No se encontró una publicación estable con formato vX.Y.Z-Public_Release.");

            if (latestVersion <= GetComparableInstalledVersion())
            {
                UpdateStatusText.Text = "Ya tienes la última versión estable (" + GetInstalledVersionLabel() + ").";
                UpdateReleaseInfoText.Text = "No se ha descargado ningún archivo.";
                return;
            }

            JsonElement exeAsset = default;
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                var assetName = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                if (string.Equals(assetName, "MarkHotspot.exe", StringComparison.OrdinalIgnoreCase))
                {
                    exeAsset = asset;
                    break;
                }
            }
            if (exeAsset.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("La publicación nueva no contiene MarkHotspot.exe.");

            var downloadUrl = exeAsset.GetProperty("browser_download_url").GetString();
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var downloadUri) ||
                downloadUri.Scheme != Uri.UriSchemeHttps ||
                !downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("El enlace de descarga de GitHub no es válido.");

            var assetSize = exeAsset.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0L;
            var notes = release.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(notes)) notes = "No se publicieron notas adicionales para esta versión.";
            var published = release.TryGetProperty("published_at", out var publishedElement) ? publishedElement.GetString() : null;
            var publishedLabel = DateTimeOffset.TryParse(published, out var publishedDate)
                ? publishedDate.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
                : "fecha no disponible";

            _availableUpdate = new AvailableUpdate(tag, downloadUrl!, assetSize, notes);

            UpdateStatusText.Text = "Actualización disponible: " + tag;
            UpdateReleaseInfoText.Text = "Publicada: " + publishedLabel + Environment.NewLine +
                "Tamaño: " + (assetSize > 0 ? FormatMegabytes(assetSize) : "No indicado") +
                Environment.NewLine + Environment.NewLine + notes;
            UpdateProgressPanel.Visibility = Visibility.Collapsed;
            CheckForUpdatesButton.Content = "Descargar e instalar";
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "No se pudo buscar actualizaciones: " + ex.Message;
            UpdateReleaseInfoText.Text = string.Empty;
            _availableUpdate = null;
            CheckForUpdatesButton.Content = "Buscar actualizaciones";
        }
        finally
        {
            CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private async Task DownloadAndInstallUpdateAsync(AvailableUpdate update)
    {
        var cancellation = new CancellationTokenSource();
        _updateCancellation = cancellation;
        string? downloadPath = Path.Combine(Path.GetTempPath(), "MarkHotspot-update-" + Guid.NewGuid().ToString("N") + ".exe");
        CheckForUpdatesButton.Content = "Cancelar descarga";
        CheckForUpdatesButton.IsEnabled = true;
        UpdateProgressPanel.Visibility = Visibility.Visible;
        UpdateProgressBar.IsIndeterminate = update.Size <= 0;
        UpdateProgressBar.Value = 0;
        UpdatePercentText.Text = update.Size > 0 ? "0 %" : "—";
        UpdateDownloadedText.Text = "Descargado: 0 MB de " + (update.Size > 0 ? FormatMegabytes(update.Size) : "—");
        UpdateSpeedText.Text = "Velocidad: calculando…";
        UpdateRemainingText.Text = "Restante: calculando…";
        UpdateStatusText.Text = "Descargando " + update.Tag + "…";

        try
        {
            using (var downloadResponse = await UpdateHttpClient.GetAsync(
                update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellation.Token))
            {
                downloadResponse.EnsureSuccessStatusCode();
                var totalBytes = update.Size > 0 ? update.Size : downloadResponse.Content.Headers.ContentLength ?? 0L;
                UpdateProgressBar.IsIndeterminate = totalBytes <= 0;
                await using var input = await downloadResponse.Content.ReadAsStreamAsync(cancellation.Token);
                await using var output = new FileStream(downloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, useAsync: true);
                var buffer = new byte[131072];
                long downloaded = 0;
                var stopwatch = Stopwatch.StartNew();
                var lastReport = TimeSpan.Zero;
                IProgress<DownloadProgress> progress = new Progress<DownloadProgress>(UpdateDownloadProgress);
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellation.Token);
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellation.Token);
                    downloaded += read;
                    if (stopwatch.Elapsed - lastReport >= TimeSpan.FromMilliseconds(200))
                    {
                        progress.Report(new DownloadProgress(downloaded, totalBytes, downloaded / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds)));
                        lastReport = stopwatch.Elapsed;
                    }
                }
                await output.FlushAsync(cancellation.Token);
                progress.Report(new DownloadProgress(downloaded, totalBytes, downloaded / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds)));
                if (update.Size > 0 && downloaded != update.Size)
                    throw new InvalidDataException("La descarga está incompleta: se recibieron " + FormatMegabytes(downloaded) + " de " + FormatMegabytes(update.Size) + ".");
            }

            cancellation.Token.ThrowIfCancellationRequested();
            UpdateStatusText.Text = "Descarga completa. Preparando la instalación…";

            // La descarga ya se comprobó mediante el tamaño publicado por GitHub.
            // No calcular SHA-256: iniciar el script de reemplazo inmediatamente.
            cancellation.Token.ThrowIfCancellationRequested();
            UpdateStatusText.Text = "Instalando " + update.Tag + " y reiniciando MarkHotspot…";
            LaunchPortableUpdater(downloadPath, update.Tag);
            downloadPath = null;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (downloadPath is not null) { try { if (File.Exists(downloadPath)) File.Delete(downloadPath); } catch { } }
            _availableUpdate = null;
            UpdateProgressPanel.Visibility = Visibility.Visible;
            UpdateProgressBar.IsIndeterminate = false;
            UpdateProgressBar.Value = 0;
            UpdatePercentText.Text = "0 %";
            UpdateDownloadedText.Text = "Descargado: 0 MB de —";
            UpdateSpeedText.Text = "Velocidad: —";
            UpdateRemainingText.Text = "Restante: —";
            UpdateStatusText.Text = "Descarga cancelada. No se instaló nada.";
            UpdateReleaseInfoText.Text = string.Empty;
            CheckForUpdatesButton.Content = "Buscar actualizaciones";
        }
        catch (Exception ex)
        {
            if (downloadPath is not null) { try { if (File.Exists(downloadPath)) File.Delete(downloadPath); } catch { } }
            UpdateStatusText.Text = "No se pudo actualizar: " + ex.Message;
            CheckForUpdatesButton.Content = "Buscar actualizaciones";
            _availableUpdate = null;
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(_updateCancellation, cancellation)) _updateCancellation = null;
            if (IsLoaded && CheckForUpdatesButton.IsEnabled)
                CheckForUpdatesButton.Content = "Buscar actualizaciones";
        }
    }

    private void UpdateDownloadProgress(DownloadProgress progress)
    {
        if (_updateCancellation?.IsCancellationRequested == true) return;
        UpdateProgressBar.IsIndeterminate = progress.Total <= 0;
        if (progress.Total > 0)
        {
            UpdateProgressBar.Value = Math.Clamp(progress.Downloaded * 100d / progress.Total, 0, 100);
            UpdatePercentText.Text = UpdateProgressBar.Value.ToString("0", CultureInfo.CurrentCulture) + " %";
        }
        else UpdatePercentText.Text = "—";

        UpdateDownloadedText.Text = "Descargado: " + FormatMegabytes(progress.Downloaded) + " de " +
            (progress.Total > 0 ? FormatMegabytes(progress.Total) : "—");
        UpdateSpeedText.Text = "Velocidad: " + FormatSpeed(progress.BytesPerSecond);
        if (progress.Total > 0 && progress.BytesPerSecond > 0)
        {
            var remainingSeconds = Math.Max(0, (progress.Total - progress.Downloaded) / progress.BytesPerSecond);
            UpdateRemainingText.Text = "Restante: " + TimeSpan.FromSeconds(remainingSeconds).ToString(@"hh\:mm\:ss");
        }
        else UpdateRemainingText.Text = "Restante: calculando…";
    }

    private static string FormatMegabytes(double bytes) =>
        (bytes / 1_000_000d).ToString("0.0", CultureInfo.CurrentCulture) + " MB";

    private static string FormatSpeed(double bytesPerSecond) =>
        bytesPerSecond >= 1_000_000d
            ? (bytesPerSecond / 1_000_000d).ToString("0.0", CultureInfo.CurrentCulture) + " MB/s"
            : (bytesPerSecond / 1_000d).ToString("0", CultureInfo.CurrentCulture) + " KB/s";

    private sealed record DownloadProgress(long Downloaded, long Total, double BytesPerSecond);

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");

    private static void LaunchPortableUpdater(string downloadedExe, string versionTag)
    {
        var targetExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(targetExe))
            throw new InvalidOperationException("No se pudo determinar la ubicación del ejecutable actual.");

        var fullTarget = Path.GetFullPath(targetExe);
        var fullDownload = Path.GetFullPath(downloadedExe);
        var safeVersion = Regex.Replace(versionTag, @"[^A-Za-z0-9._-]", "_");
        var targetDirectory = Path.GetDirectoryName(fullTarget)
            ?? throw new InvalidOperationException("No se pudo determinar la carpeta de MarkHotspot.");
        var targetBaseName = Path.GetFileNameWithoutExtension(fullTarget);
        var newTarget = Path.Combine(targetDirectory, targetBaseName + "-" + safeVersion + ".exe");
        if (string.Equals(fullTarget, newTarget, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("La versión nueva tiene el mismo nombre que el ejecutable actual.");

        var scriptPath = Path.Combine(Path.GetTempPath(), "MarkHotspot-update-" + Guid.NewGuid().ToString("N") + ".ps1");
        var script = string.Join(Environment.NewLine, new[]
        {
            "$ErrorActionPreference = 'Stop'",
            "$target = '__TARGET__'",
            "$newTarget = '__NEW_TARGET__'",
            "$download = '__DOWNLOAD__'",
            "$staged = $newTarget + '.staging.exe'",
            "__PID__",
            "try {",
            "  for ($i = 0; $i -lt 180; $i++) { if (-not (Get-Process -Id $processId -ErrorAction SilentlyContinue)) { break }; Start-Sleep -Milliseconds 500 }",
            "  if (Get-Process -Id $processId -ErrorAction SilentlyContinue) { throw 'MarkHotspot no se cerró a tiempo.' }",
            "  if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw ('No existe el ejecutable actual: ' + $target) }",
            "  if (-not (Test-Path -LiteralPath $download -PathType Leaf)) { throw ('No existe el archivo descargado: ' + $download) }",
            "  Copy-Item -LiteralPath $download -Destination $staged -Force",
            "  Move-Item -LiteralPath $staged -Destination $newTarget -Force",
            "  $newProcess = Start-Process -FilePath $newTarget -PassThru",
            "  Start-Sleep -Seconds 5",
            "  if (-not (Get-Process -Id $newProcess.Id -ErrorAction SilentlyContinue)) { throw 'La nueva versión no permaneció abierta; se intentará restaurar la versión anterior.' }",
            "  if (Test-Path -LiteralPath $target -PathType Leaf) { Remove-Item -LiteralPath $target -Force }",
            "  Remove-Item -LiteralPath $download -Force -ErrorAction SilentlyContinue",
            "} catch {",
            "  $failure = $_.Exception.Message",
            "  try { if (Test-Path -LiteralPath $target -PathType Leaf) { Start-Process -FilePath $target } } catch {}",
            "  try { if (Test-Path -LiteralPath $newTarget -PathType Leaf) { Remove-Item -LiteralPath $newTarget -Force -ErrorAction SilentlyContinue } } catch {}",
            "  try { Add-Type -AssemblyName PresentationFramework; [System.Windows.MessageBox]::Show(('La actualización falló. Se mantuvo o reinició la versión anterior.' + [Environment]::NewLine + $failure + [Environment]::NewLine + 'Ejecutable: ' + $target), 'MarkHotspot', 'OK', 'Error') | Out-Null } catch {}",
            "} finally {",
            "  Remove-Item -LiteralPath $staged -Force -ErrorAction SilentlyContinue",
            "  Remove-Item -LiteralPath $download -Force -ErrorAction SilentlyContinue",
            "  try { Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue } catch {}",
            "}"
        }).Replace("__TARGET__", EscapePowerShellLiteral(fullTarget))
          .Replace("__NEW_TARGET__", EscapePowerShellLiteral(newTarget))
          .Replace("__DOWNLOAD__", EscapePowerShellLiteral(fullDownload))
          .Replace("__PID__", "$processId = " + Environment.ProcessId);

        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);

        try
        {
            if (Process.Start(startInfo) is null)
                throw new InvalidOperationException("Windows no pudo iniciar el script temporal de actualización.");
        }
        catch
        {
            try { File.Delete(scriptPath); } catch { }
            throw;
        }

        foreach (Window window in System.Windows.Application.Current.Windows)
        {
            if (window is MainWindow mainWindow)
            {
                mainWindow._allowExit = true;
                mainWindow.Close();
                break;
            }
        }
    }
}
