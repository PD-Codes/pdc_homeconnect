using System.Text.Json.Nodes;
using Microsoft.Win32;
using PdcHomeConnect.Agent.Native;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;
using N = PdcHomeConnect.Agent.Native.Native;

namespace PdcHomeConnect.Agent.Modules;

/// <summary>Monitor brightness (DDC/CI and laptop panels) and the Windows dark mode switch.</summary>
public sealed class DisplayModule : EntityProvider, IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly AgentContext _context;
    private readonly SemaphoreSlim _monitorGate = new(1, 1);
    private List<IBrightnessMonitor> _monitors = [];
    private RegistryWatcher? _themeWatcher;
    private string _signature = string.Empty;

    public DisplayModule(AgentContext context, ILogger<DisplayModule> logger)
        : base("display", context.Hub, logger)
    {
        _context = context;
    }

    private FeaturesConfig Features => _context.Config.Features;

    public override async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Features.DarkMode)
        {
            _themeWatcher = new RegistryWatcher(RegistryHive.CurrentUser, PersonalizeKey, TimeSpan.FromMilliseconds(300));
            _themeWatcher.Changed += UpdateDarkMode;
            _themeWatcher.Start();
        }

        if (!Features.Brightness)
        {
            PublishAll();
            UpdateDarkMode();
            return;
        }

        var tick = 0;
        await RunPeriodicAsync(TimeSpan.FromSeconds(60), async ct =>
        {
            await _monitorGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Re-enumerate every 10 minutes (and at start); monitors may be plugged in or out.
                if (tick++ % 10 == 0)
                {
                    Enumerate();
                }

                for (var i = 0; i < _monitors.Count; i++)
                {
                    Set(Key(i), _monitors[i].GetBrightness(), Attrs(("display", _monitors[i].Name), ("method", _monitors[i].Kind)));
                }
            }
            finally
            {
                _monitorGate.Release();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string Key(int index) => $"monitor_{index + 1}_brightness";

    private void Enumerate()
    {
        var monitors = Monitors.Enumerate();
        var signature = string.Join("|", monitors.Select(m => m.Kind + ":" + m.Name));
        foreach (var old in _monitors)
        {
            old.Dispose();
        }

        _monitors = monitors;
        if (signature != _signature)
        {
            _signature = signature;
            Logger.LogInformation("Displays with brightness control: {Count}", monitors.Count);
            PublishAll();
        }

        UpdateDarkMode();
    }

    private void PublishAll()
    {
        var descriptors = new List<EntityDescriptor>();
        if (Features.DarkMode)
        {
            descriptors.Add(new() { Key = "dark_mode", Platform = Platforms.Switch, TranslationKey = "dark_mode", Name = "Dark mode" });
        }

        for (var i = 0; i < _monitors.Count; i++)
        {
            var label = _monitors.Count == 1 ? "1" : $"{i + 1}";
            descriptors.Add(new()
            {
                Key = Key(i),
                Platform = Platforms.Number,
                TranslationKey = "monitor_brightness",
                Placeholders = new() { ["monitor"] = label },
                Name = $"Display {label} brightness",
                Unit = "%",
                Min = 0,
                Max = 100,
                Step = 1,
                Mode = "slider",
            });
        }

        Publish(descriptors);
    }

    private void UpdateDarkMode()
    {
        if (!Features.DarkMode)
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        var appsLight = key?.GetValue("AppsUseLightTheme") is int apps ? apps : 1;
        var systemLight = key?.GetValue("SystemUsesLightTheme") is int system ? system : 1;
        Set("dark_mode", appsLight == 0, Attrs(("apps_dark", appsLight == 0), ("system_dark", systemLight == 0)));
    }

    private static void SetDarkMode(bool dark)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey))
        {
            key.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
        }

        // Tell Explorer and running apps to re-read the theme.
        N.SendMessageTimeout(N.HwndBroadcast, N.WmSettingChange, 0, "ImmersiveColorSet", 0x0002, 200, out _);
    }

    public override async Task<CommandResult> HandleCommandAsync(EntityCommand command, CancellationToken cancellationToken)
    {
        if (command.Key == "dark_mode" && command.Action is Actions.TurnOn or Actions.TurnOff)
        {
            await Task.Run(() => SetDarkMode(command.Action == Actions.TurnOn), cancellationToken).ConfigureAwait(false);
            UpdateDarkMode();
            return CommandResult.Ok();
        }

        if (command.Action == Actions.SetValue && command.Key.StartsWith("monitor_", StringComparison.Ordinal))
        {
            var value = (int)Math.Round(command.Params.GetDouble("value") ?? 0);
            await _monitorGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var index = _monitors.FindIndex((m, i) => Key(i) == command.Key);
                if (index < 0)
                {
                    return CommandResult.Fail(ErrorCodes.NotFound, "Display not found.");
                }

                var monitor = _monitors[index];
                var ok = await Task.Run(() => monitor.SetBrightness(value), cancellationToken).ConfigureAwait(false);
                if (!ok)
                {
                    return CommandResult.Fail(ErrorCodes.Failed, "The display did not accept the brightness (is DDC/CI enabled in the monitor menu?).");
                }

                Set(command.Key, (long)value, Attrs(("display", monitor.Name), ("method", monitor.Kind)));
                return CommandResult.Ok();
            }
            finally
            {
                _monitorGate.Release();
            }
        }

        return CommandResult.NotSupported(command.Action);
    }

    public void Dispose()
    {
        _themeWatcher?.Dispose();
        foreach (var monitor in _monitors)
        {
            monitor.Dispose();
        }
    }
}

internal static class ListExtensions
{
    public static int FindIndex<T>(this List<T> list, Func<T, int, bool> predicate)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (predicate(list[i], i))
            {
                return i;
            }
        }

        return -1;
    }
}
