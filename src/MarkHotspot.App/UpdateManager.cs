using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace MarkHotspot;

public partial class MainWindow
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/marquito1611/MarkHospotApp/releases/latest";
    private static readonly HttpClient UpdateHttpClient = CreateUpdateHttpClient();

    private static HttpClient CreateUpdateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MarkHotspot-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static Version GetInstalledAssemblyVersion() =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0, 0);

    private static int GetInstalledBuildNumber()
    {
        var version = GetInstalledAssemblyVersion();
        return version.Revision >= 0 ? version.Revision : Math.Max(0, version.Build);
    }

    private static string GetInstalledVersionLabel()
    {
        var build = GetInstalledBuildNumber();
        return build > 0 ? "v1.0.0-build" + build : "versión " + GetInstalledAssemblyVersion();
    }

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync();

    private async Task CheckForUpdatesAsync()
    {
        CheckForUpdatesButton.IsEnabled = false;
        UpdateProgressPanel.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "Buscando la última versión estable en GitHub…";
        string? downloadPath = null;
        try
        {
            using var apiResponse = await UpdateHttpClient.GetAsync(LatestReleaseApiUrl, HttpCompletionOption.ResponseHeadersRead);
            apiResponse.EnsureSuccessStatusCode();
            using var releaseDocument = JsonDocument.Parse(await apiResponse.Content.ReadAsStringAsync());
            var release = releaseDocument.RootElement;
            var tag = release.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag))
                throw new InvalidDataException("GitHub no devolvió una etiqueta de versión válida.");

            var buildMatch = Regex.Match(tag, @"build([0-9]+)$", RegexOptions.IgnoreCase);
            if (!buildMatch.Success || !int.TryParse(buildMatch.Groups[1].Value, out var latestBuild))
                throw new InvalidDataException("La publicación más reciente no tiene el formato esperado (v1.0.0-buildN).");

            if ((release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) ||
                (release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()))
                throw new InvalidDataException("La publicación encontrada no es una versión estable.");

            if (latestBuild <= GetInstalledBuildNumber())
            {
                UpdateStatusText.Text = "Ya tienes la última versión estable (" + tag + ").";
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
            var digest = exeAsset.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GitHub no proporcionó el SHA-256 de la descarga; se canceló la actualización por seguridad.");

            // The progress panel stays hidden unless a newer stable release was found.
            UpdateProgressPanel.Visibility = Visibility.Visible;
            UpdateProgressBar.IsIndeterminate = assetSize <= 0;
            UpdateProgressBar.Value = 0;
            UpdatePercentText.Text = assetSize > 0 ? "0 %" : "—";
            UpdateDownloadedText.Text = "Descargado: 0 MB de " + (assetSize > 0 ? FormatMegabytes(assetSize) : "—");
            UpdateSpeedText.Text = "Velocidad: calculando…";
            UpdateRemainingText.Text = "Restante: calculando…";
            UpdateStatusText.Text = "Nueva versión " + tag + " disponible. Descargando…";

            downloadPath = Path.Combine(Path.GetTempPath(), "MarkHotspot-update-" + Guid.NewGuid().ToString("N") + ".exe");
            using (var downloadResponse = await UpdateHttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                downloadResponse.EnsureSuccessStatusCode();
                var totalBytes = assetSize > 0 ? assetSize : downloadResponse.Content.Headers.ContentLength ?? 0L;
                UpdateProgressBar.IsIndeterminate = totalBytes <= 0;
                await using var input = await downloadResponse.Content.ReadAsStreamAsync();
                await using var output = new FileStream(downloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, useAsync: true);
                var buffer = new byte[131072];
                long downloaded = 0;
                var stopwatch = Stopwatch.StartNew();
                var lastReport = TimeSpan.Zero;
                IProgress<DownloadProgress> progress = new Progress<DownloadProgress>(UpdateDownloadProgress);
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length));
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    downloaded += read;
                    if (stopwatch.Elapsed - lastReport >= TimeSpan.FromMilliseconds(200))
                    {
                        progress.Report(new DownloadProgress(downloaded, totalBytes, downloaded / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds)));
                        lastReport = stopwatch.Elapsed;
                    }
                }
                await output.FlushAsync();
                progress.Report(new DownloadProgress(downloaded, totalBytes, downloaded / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds)));
                if (assetSize > 0 && downloaded != assetSize)
                    throw new InvalidDataException("La descarga está incompleta: se recibieron " + FormatMegabytes(downloaded) + " de " + FormatMegabytes(assetSize) + ".");
            }

            UpdateStatusText.Text = "Descarga completa. Verificando integridad…";
            string actualHash;
            await using (var downloadedFile = File.OpenRead(downloadPath))
                actualHash = Convert.ToHexString(await SHA256.HashDataAsync(downloadedFile));
            var expectedHash = digest.Substring("sha256:".Length);
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("El SHA-256 del archivo no coincide con el publicado por GitHub. No se instalará.");

            UpdateStatusText.Text = "Actualización " + tag + " verificada. Reiniciando MarkHotspot…";
            LaunchPortableUpdater(downloadPath);
            downloadPath = null; // The temporary script owns cleanup after this point.
        }
        catch (Exception ex)
        {
            if (downloadPath is not null)
            {
                try { if (File.Exists(downloadPath)) File.Delete(downloadPath); } catch { }
            }
            UpdateStatusText.Text = "No se pudo actualizar: " + ex.Message;
        }
        finally
        {
            CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private void UpdateDownloadProgress(DownloadProgress progress)
    {
        UpdateProgressBar.IsIndeterminate = progress.Total <= 0;
        if (progress.Total > 0)
        {
            UpdateProgressBar.Value = Math.Clamp(progress.Downloaded * 100d / progress.Total, 0, 100);
            UpdatePercentText.Text = UpdateProgressBar.Value.ToString("0", CultureInfo.CurrentCulture) + "%";
        }
        else UpdatePercentText.Text = "—";

        UpdateDownloadedText.Text = "Descargado: " + FormatMegabytes(progress.Downloaded) + " de " +
            (progress.Total > 0 ? FormatMegabytes(progress.Total) : "—");
        UpdateSpeedText.Text = "Velocidad: " + FormatSpeed(progress.BytesPerSecond);
        if (progress.Total > 0 && progress.BytesPerSecond > 0)
        {
            var remainingSeconds = Math.Max(0, (progress.Total - progress.Downloaded) / progress.BytesPerSecond);
            UpdateRemainingText.Text = "Restante: " + TimeSpan.FromSeconds(remainingSeconds).ToString("c");
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

    private static void LaunchPortableUpdater(string downloadedExe)
    {
        var targetExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(targetExe))
            throw new InvalidOperationException("No se pudo determinar la ubicación del ejecutable actual.");

        var scriptPath = Path.Combine(Path.GetTempPath(), "MarkHotspot-update-" + Guid.NewGuid().ToString("N") + ".ps1");
        var script = string.Join(Environment.NewLine, new[]
        {
            "$ErrorActionPreference = 'Stop'",
            "$target = '__TARGET__'",
            "$download = '__DOWNLOAD__'",
            "$staged = $target + '.update'",
            "$backup = $target + '.backup'",
            "__PID__",
            "try {",
            "  for ($i = 0; $i -lt 180; $i++) { if (-not (Get-Process -Id $processId -ErrorAction SilentlyContinue)) { break }; Start-Sleep -Milliseconds 500 }",
            "  if (Get-Process -Id $processId -ErrorAction SilentlyContinue) { throw 'MarkHotspot no se cerró a tiempo.' }",
            "  Copy-Item -LiteralPath $download -Destination $staged -Force",
            "  Copy-Item -LiteralPath $target -Destination $backup -Force",
            "  Move-Item -LiteralPath $staged -Destination $target -Force",
            "  $newProcess = Start-Process -FilePath $target -PassThru",
            "  Start-Sleep -Seconds 5",
            "  if (-not (Get-Process -Id $newProcess.Id -ErrorAction SilentlyContinue)) { throw 'La nueva versión no permaneció abierta; se restaurará la versión anterior.' }",
            "  Remove-Item -LiteralPath $backup -Force",
            "  Remove-Item -LiteralPath $download -Force -ErrorAction SilentlyContinue",
            "} catch {",
            "  try { if (Test-Path -LiteralPath $backup) { if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }; Copy-Item -LiteralPath $backup -Destination $target -Force; Start-Process -FilePath $target } } catch {}",
            "  try { Add-Type -AssemblyName PresentationFramework; [System.Windows.MessageBox]::Show(('La actualización falló. Se intentó restaurar la versión anterior.' + [Environment]::NewLine + $_.Exception.Message), 'MarkHotspot', 'OK', 'Error') | Out-Null } catch {}",
            "} finally {",
            "  Remove-Item -LiteralPath $staged -Force -ErrorAction SilentlyContinue",
            "  Remove-Item -LiteralPath $download -Force -ErrorAction SilentlyContinue",
            "  try { Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue } catch {}",
            "}"
        }).Replace("__TARGET__", EscapePowerShellLiteral(Path.GetFullPath(targetExe)))
          .Replace("__DOWNLOAD__", EscapePowerShellLiteral(Path.GetFullPath(downloadedExe)))
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

        // Let the script replace the EXE only after this process exits.
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
