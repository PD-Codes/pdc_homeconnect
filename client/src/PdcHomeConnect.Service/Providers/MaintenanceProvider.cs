using System.Management;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;

namespace PdcHomeConnect.Service.Providers;

/// <summary>Pending restart, Windows updates and Microsoft Defender status.</summary>
public sealed class MaintenanceProvider : EntityProvider
{
    private readonly ServiceConfig _config;

    public MaintenanceProvider(EntityHub hub, ServiceConfig config, ILogger<MaintenanceProvider> logger)
        : base("maintenance", hub, logger)
    {
        _config = config;
    }

    public override Task RunAsync(CancellationToken cancellationToken)
    {
        var updates = ModuleState.IsOn(_config.Modules.WindowsUpdate, true);
        var defender = ModuleState.IsOn(_config.Modules.Defender, true) && DefenderAvailable();
        var list = new List<EntityDescriptor>
        {
            new() { Key = "pending_reboot", Platform = Platforms.BinarySensor, TranslationKey = "pending_reboot", Name = "Restart pending", EntityCategory = Ha.Diagnostic },
        };
        if (updates)
        {
            list.Add(new() { Key = "windows_updates", Platform = Platforms.Sensor, TranslationKey = "windows_updates", Name = "Windows updates", StateClass = Ha.Measurement, Precision = 0, EntityCategory = Ha.Diagnostic });
        }

        if (defender)
        {
            list.Add(new()
            {
                Key = "defender_status",
                Platform = Platforms.Sensor,
                TranslationKey = "defender_status",
                Name = "Defender",
                DeviceClass = "enum",
                Options = ["ok", "warning", "outdated", "disabled", "action_required", "passive", "unknown"],
                EntityCategory = Ha.Diagnostic,
            });
        }

        Publish(list);

        var tasks = new List<Task>
        {
            RunPeriodicAsync(TimeSpan.FromMinutes(5), _ =>
            {
                var reasons = PendingRebootReasons();
                Set("pending_reboot", reasons.Count > 0, new JsonObject { ["reasons"] = new JsonArray(reasons.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()) });
                return Task.CompletedTask;
            }, cancellationToken),
        };

        if (defender)
        {
            tasks.Add(RunPeriodicAsync(TimeSpan.FromMinutes(30), _ =>
            {
                UpdateDefender();
                return Task.CompletedTask;
            }, cancellationToken));
        }

        if (updates)
        {
            tasks.Add(RunUpdatesAsync(cancellationToken));
        }

        return Task.WhenAll(tasks);
    }

    private static List<string> PendingRebootReasons()
    {
        var reasons = new List<string>();
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        if (hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") is { } cbs)
        {
            cbs.Dispose();
            reasons.Add("component_based_servicing");
        }

        if (hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") is { } wu)
        {
            wu.Dispose();
            reasons.Add("windows_update");
        }

        using (var sessionManager = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
        {
            if (sessionManager?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 })
            {
                reasons.Add("file_rename_operations");
            }
        }

        using (var updates = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Updates"))
        {
            if (updates?.GetValue("UpdateExeVolatile") is int volatileFlag && volatileFlag != 0)
            {
                reasons.Add("update_exe_volatile");
            }
        }

        using (var active = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName"))
        using (var pending = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName"))
        {
            if (active?.GetValue("ComputerName") is string a && pending?.GetValue("ComputerName") is string p &&
                !a.Equals(p, StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("computer_rename");
            }
        }

        return reasons;
    }

    private async Task RunUpdatesAsync(CancellationToken cancellationToken)
    {
        // Let the system settle after boot before the (slow) Windows Update search.
        await Task.Delay(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        await RunPeriodicAsync(() => TimeSpan.FromHours(Math.Clamp(_config.WindowsUpdateCheckHours, 1, 168)), async ct =>
        {
            // The Windows Update Agent COM API is synchronous and can take minutes.
            var result = await Task.Factory.StartNew(SearchUpdates, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
            if (result is not null)
            {
                Set("windows_updates", result.Value.Count, result.Value.Attributes);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private (long Count, JsonObject Attributes)? SearchUpdates()
    {
        try
        {
            var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (sessionType is null)
            {
                return null;
            }

            dynamic session = Activator.CreateInstance(sessionType)!;
            session.ClientApplicationID = "PDC HomeConnect";
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic searchResult = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'");
            dynamic updates = searchResult.Updates;
            int count = updates.Count;
            var titles = new JsonArray();
            var security = 0;
            for (var i = 0; i < count; i++)
            {
                dynamic update = updates.Item(i);
                string title = update.Title;
                if (titles.Count < 25)
                {
                    titles.Add(title);
                }

                string? severity = update.MsrcSeverity;
                if (!string.IsNullOrEmpty(severity))
                {
                    security++;
                }
            }

            var attributes = new JsonObject
            {
                ["updates"] = titles,
                ["security_updates"] = security,
                ["last_check"] = DateTimeOffset.UtcNow.ToString("O"),
            };

            try
            {
                var autoType = Type.GetTypeFromProgID("Microsoft.Update.AutoUpdate");
                if (autoType is not null)
                {
                    dynamic auto = Activator.CreateInstance(autoType)!;
                    dynamic results = auto.Results;
                    object installed = results.LastInstallationSuccessDate;
                    if (installed is DateTime installedAt && installedAt.Year > 2000)
                    {
                        attributes["last_install"] = new DateTimeOffset(DateTime.SpecifyKind(installedAt, DateTimeKind.Utc)).ToString("O");
                    }
                }
            }
            catch
            {
                // Optional information.
            }

            return (count, attributes);
        }
        catch (Exception ex)
        {
            Logger.LogInformation("Windows Update search failed: {Message}", ex.Message);
            return null;
        }
    }

    private static bool DefenderAvailable()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Defender", "SELECT AMServiceEnabled FROM MSFT_MpComputerStatus");
            return searcher.Get().Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateDefender()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Defender", "SELECT * FROM MSFT_MpComputerStatus");
            foreach (ManagementBaseObject status in searcher.Get())
            {
                var serviceEnabled = status["AMServiceEnabled"] as bool? ?? false;
                var antivirusEnabled = status["AntivirusEnabled"] as bool? ?? false;
                var realtime = status["RealTimeProtectionEnabled"] as bool? ?? false;
                var signatureAge = Convert.ToInt64(status["AntivirusSignatureAge"] ?? 0);
                var computerState = Convert.ToInt64(status["ComputerState"] ?? 0);
                var mode = status["AMRunningMode"] as string ?? string.Empty;
                var quickScanAge = status["QuickScanAge"] is { } q ? Convert.ToInt64(q) : (long?)null;
                var fullScanAge = status["FullScanAge"] is { } f ? Convert.ToInt64(f) : (long?)null;
                var tamper = status["IsTamperProtected"] as bool?;

                string state;
                if (mode.Contains("Passive", StringComparison.OrdinalIgnoreCase) || mode.Contains("EDR", StringComparison.OrdinalIgnoreCase))
                {
                    state = "passive";
                }
                else if (!serviceEnabled || !antivirusEnabled)
                {
                    state = "disabled";
                }
                else if (computerState != 0)
                {
                    state = "action_required";
                }
                else if (!realtime)
                {
                    state = "warning";
                }
                else if (signatureAge > 3 && signatureAge < 4294967295)
                {
                    state = "outdated";
                }
                else
                {
                    state = "ok";
                }

                Set("defender_status", state, Attrs(
                    ("realtime_protection", realtime),
                    ("signature_age_days", signatureAge < 4294967295 ? signatureAge : null),
                    ("quick_scan_age_days", quickScanAge < 4294967295 ? quickScanAge : null),
                    ("full_scan_age_days", fullScanAge < 4294967295 ? fullScanAge : null),
                    ("tamper_protection", tamper),
                    ("mode", mode)));
                return;
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Defender status unavailable");
            Set("defender_status", "unknown");
        }
    }
}
