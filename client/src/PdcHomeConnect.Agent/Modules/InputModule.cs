using System.Text.Json.Nodes;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;
using PdcHomeConnect.Core.Util;
using N = PdcHomeConnect.Agent.Native.Native;

namespace PdcHomeConnect.Agent.Modules;

/// <summary>Global hotkeys and tray menu actions as event entities (triggers for Home Assistant automations).</summary>
public sealed class InputModule : EntityProvider, IDisposable
{
    private const uint ModAlt = 0x1;
    private const uint ModControl = 0x2;
    private const uint ModShift = 0x4;
    private const uint ModWin = 0x8;
    private const uint ModNoRepeat = 0x4000;

    private readonly AgentContext _context;
    private readonly Dictionary<int, string> _hotkeys = [];

    public InputModule(AgentContext context, ILogger<InputModule> logger)
        : base("input", context.Hub, logger)
    {
        _context = context;
    }

    public IReadOnlyList<TrayActionConfig> TrayActions =>
        _context.Config.TrayActions.Where(a => !string.IsNullOrWhiteSpace(a.Id)).ToList();

    public override async Task RunAsync(CancellationToken cancellationToken)
    {
        var hotkeys = _context.Config.Hotkeys.Where(h => !string.IsNullOrWhiteSpace(h.Id) && !string.IsNullOrWhiteSpace(h.Keys)).ToList();
        var descriptors = new List<EntityDescriptor>();
        if (hotkeys.Count > 0)
        {
            descriptors.Add(new()
            {
                Key = "hotkey",
                Platform = Platforms.Event,
                TranslationKey = "hotkey",
                Name = "Hotkey",
                EventTypes = hotkeys.Select(h => Slug.From(h.Id)).Distinct().ToList(),
            });
        }

        if (TrayActions.Count > 0)
        {
            descriptors.Add(new()
            {
                Key = "tray_action",
                Platform = Platforms.Event,
                TranslationKey = "tray_action",
                Name = "Tray action",
                EventTypes = TrayActions.Select(a => Slug.From(a.Id)).Distinct().ToList(),
            });
        }

        Publish(descriptors);
        if (hotkeys.Count == 0)
        {
            return;
        }

        // RegisterHotKey binds to the thread that owns the window: run on the UI thread.
        await _context.Ui.InvokeAsync(() =>
        {
            var id = 1;
            foreach (var hotkey in hotkeys)
            {
                if (!TryParse(hotkey.Keys, out var modifiers, out var key))
                {
                    Logger.LogWarning("Invalid hotkey '{Keys}' for {Id}", hotkey.Keys, hotkey.Id);
                    continue;
                }

                if (N.RegisterHotKey(_context.Window.Handle, id, modifiers | ModNoRepeat, (uint)key))
                {
                    _hotkeys[id] = Slug.From(hotkey.Id);
                    id++;
                }
                else
                {
                    Logger.LogWarning("Hotkey {Keys} is already used by another app", hotkey.Keys);
                }
            }

            _context.Window.HotKeyPressed += OnHotKey;
        }).ConfigureAwait(false);
    }

    private void OnHotKey(int id)
    {
        if (_hotkeys.TryGetValue(id, out var name))
        {
            Logger.LogDebug("Hotkey {Name}", name);
            Hub.FireEvent("hotkey", name);
            Hub.FireBusEvent("hotkey", new JsonObject { ["hotkey"] = name });
        }
    }

    /// <summary>Called by the tray menu.</summary>
    public void FireTrayAction(string id)
    {
        var name = Slug.From(id);
        Hub.FireEvent("tray_action", name);
        Hub.FireBusEvent("tray_action", new JsonObject { ["action"] = name });
    }

    /// <summary>Parses "Ctrl+Alt+L", "Win+Shift+F5", "Ctrl+Alt+1".</summary>
    public static bool TryParse(string text, out uint modifiers, out Keys key)
    {
        modifiers = 0;
        key = Keys.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control" or "strg":
                    modifiers |= ModControl;
                    continue;
                case "alt":
                    modifiers |= ModAlt;
                    continue;
                case "shift":
                    modifiers |= ModShift;
                    continue;
                case "win" or "windows" or "super":
                    modifiers |= ModWin;
                    continue;
            }

            if (key != Keys.None)
            {
                return false;
            }

            var token = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
            token = token.ToLowerInvariant() switch
            {
                "esc" => "Escape",
                "del" => "Delete",
                "ins" => "Insert",
                "pgup" => "PageUp",
                "pgdn" or "pgdown" => "PageDown",
                "enter" => "Return",
                _ => token,
            };
            if (!Enum.TryParse(token, ignoreCase: true, out key) || key is Keys.None or Keys.Modifiers)
            {
                return false;
            }
        }

        return key != Keys.None && modifiers != 0 || key is >= Keys.F13 and <= Keys.F24 or >= Keys.BrowserBack and <= Keys.LaunchApplication2;
    }

    public void Dispose()
    {
        var ids = _hotkeys.Keys.ToList();
        _context.Ui.Post(() =>
        {
            foreach (var id in ids)
            {
                N.UnregisterHotKey(_context.Window.Handle, id);
            }
        });
    }
}
