using System.Media;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Toolkit.Uwp.Notifications;
using PdcHomeConnect.Agent.Media;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;
using Windows.UI.Notifications;

namespace PdcHomeConnect.Agent.Modules;

/// <summary>
/// Windows toast notifications from Home Assistant (actions, reply, images, scenarios), the pairing code toast,
/// the "speak" action and the notification action event.
/// </summary>
public sealed class NotificationModule : EntityProvider
{
    private const string Group = "pdc";
    private const string SystemGroup = "pdc-system";
    private const string PairingTag = "pairing";

    private readonly AgentContext _context;
    private PairingInfo? _pairing;

    public NotificationModule(AgentContext context, ILogger<NotificationModule> logger)
        : base("notifications", context.Hub, logger)
    {
        _context = context;
    }

    public override Task RunAsync(CancellationToken cancellationToken)
    {
        Publish(
        [
            new()
            {
                Key = "notification_action",
                Platform = Platforms.Event,
                TranslationKey = "notification_action",
                Name = "Notification action",
                EventTypes = ["action", "reply", "dismissed"],
            },
        ]);

        Hub.RegisterMethod("notify", NotifyAsync);
        Hub.RegisterMethod("clear_notifications", ClearAsync);
        Hub.RegisterMethod("speak", SpeakAsync);
        ToastNotificationManagerCompat.OnActivated += OnActivated;
        CleanupImages();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ notify

    private async Task<CommandResult> NotifyAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var message = parameters.GetString("message") ?? string.Empty;
        var title = parameters.GetString("title");
        var tag = parameters.GetString("tag");
        tag = string.IsNullOrWhiteSpace(tag) ? Guid.NewGuid().ToString("N")[..16] : tag.Trim();
        if (tag.Length > 64)
        {
            tag = tag[..64];
        }

        var builder = new ToastContentBuilder().AddArgument("tag", tag);
        if (!string.IsNullOrWhiteSpace(title))
        {
            builder.AddText(title);
        }

        builder.AddText(message);
        builder.AddAttributionText("Home Assistant");

        if (parameters.GetObject("image") is { Count: > 0 } image && image.GetString("data") is { } imageData)
        {
            var path = SaveImage(Convert.FromBase64String(imageData), image.GetString("content_type"));
            builder.AddInlineImage(new Uri(path));
        }

        var actions = parameters.GetArray("actions")?.OfType<JsonObject>().Take(5).ToList() ?? [];
        var reply = parameters.GetString("reply");
        if (!string.IsNullOrWhiteSpace(reply))
        {
            builder.AddInputTextBox("reply", placeHolderContent: reply);
            builder.AddButton(new ToastButton()
                .SetContent("Send")
                .AddArgument("action", "reply")
                .SetTextBoxId("reply"));
        }

        foreach (var action in actions.Take(string.IsNullOrWhiteSpace(reply) ? 5 : 4))
        {
            builder.AddButton(new ToastButton()
                .SetContent(action.GetString("title") ?? action.GetString("action") ?? "OK")
                .AddArgument("action", action.GetString("action") ?? string.Empty));
        }

        var scenario = parameters.GetString("scenario") ?? "default";
        var hasButtons = actions.Count > 0 || !string.IsNullOrWhiteSpace(reply);
        if (scenario is "reminder" or "alarm" && !hasButtons)
        {
            // Reminder/alarm toasts need a button, otherwise Windows shows them as normal toasts.
            builder.AddButton(new ToastButtonDismiss());
        }

        var sound = parameters.GetString("sound") ?? "default";
        var audio = sound switch
        {
            "silent" => new ToastAudio { Silent = true },
            "im" => new ToastAudio { Src = new Uri("ms-winsoundevent:Notification.IM") },
            "mail" => new ToastAudio { Src = new Uri("ms-winsoundevent:Notification.Mail") },
            "reminder" => new ToastAudio { Src = new Uri("ms-winsoundevent:Notification.Reminder") },
            "sms" => new ToastAudio { Src = new Uri("ms-winsoundevent:Notification.SMS") },
            "alarm" => new ToastAudio { Src = new Uri("ms-winsoundevent:Notification.Looping.Alarm"), Loop = true },
            "call" => new ToastAudio { Src = new Uri("ms-winsoundevent:Notification.Looping.Call"), Loop = true },
            _ => null,
        };
        if (audio is not null)
        {
            builder.AddAudio(audio);
        }

        var duration = parameters.GetString("duration") == "long" || audio?.Loop == true ? ToastDuration.Long : ToastDuration.Short;
        builder.SetToastDuration(duration);

        var xml = builder.GetToastContent().GetXml();
        if (scenario != "default" && xml.DocumentElement is { } root)
        {
            // Set via XML: "urgent" is newer than the toolkit's ToastScenario enum.
            root.SetAttribute("scenario", scenario);
        }

        var toast = new ToastNotification(xml) { Tag = tag, Group = Group };
        toast.Dismissed += (_, args) =>
        {
            if (args.Reason == ToastDismissalReason.UserCanceled)
            {
                Fire("dismissed", Attrs(("tag", tag), ("title", title)));
            }
        };

        await _context.Ui.InvokeAsync(() => ToastNotificationManagerCompat.CreateToastNotifier().Show(toast)).ConfigureAwait(false);

        // Windows hides toasts while a fullscreen app/game runs or focus assist is on.
        var fallback = parameters.GetString("fullscreen_fallback") ?? "auto";
        if (fallback == "auto")
        {
            fallback = _context.Config.Notifications.FullscreenFallback;
        }

        if (fallback is "sound" or "speak" && IsSuppressed())
        {
            if (fallback == "speak")
            {
                await AudioPlayback.SpeakAsync(string.IsNullOrWhiteSpace(title) ? message : $"{title}. {message}", null).ConfigureAwait(false);
            }
            else if (sound != "silent")
            {
                SystemSounds.Asterisk.Play();
            }
        }

        return CommandResult.Ok(new JsonObject { ["tag"] = tag });
    }

    private bool IsSuppressed() => _context.Signals.Fullscreen is "fullscreen" or "game" or "presentation" or "quiet_time";

    private Task<CommandResult> ClearAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var tag = parameters.GetString("tag");
        if (string.IsNullOrWhiteSpace(tag))
        {
            ToastNotificationManagerCompat.History.RemoveGroup(Group);
        }
        else
        {
            ToastNotificationManagerCompat.History.Remove(tag, Group);
        }

        return Task.FromResult(CommandResult.Ok());
    }

    private async Task<CommandResult> SpeakAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var message = parameters.GetString("message");
        if (string.IsNullOrWhiteSpace(message))
        {
            return CommandResult.Fail(ErrorCodes.InvalidParams, "message is required.");
        }

        await AudioPlayback.SpeakAsync(message, parameters.GetString("language")).ConfigureAwait(false);
        return CommandResult.Ok();
    }

    // ------------------------------------------------------------------ activation

    private void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            var arguments = ToastArguments.Parse(e.Argument);
            arguments.TryGetValue("action", out var action);
            arguments.TryGetValue("tag", out var tag);

            if (action == "copy_pairing_code")
            {
                var code = _pairing?.Code;
                if (code is not null)
                {
                    _context.Ui.Post(() => Clipboard.SetText(code));
                }
                return;
            }

            if (arguments.Contains("system"))
            {
                return;
            }

            if (action == "reply")
            {
                var text = e.UserInput.TryGetValue("reply", out var value) ? value as string : null;
                Fire("reply", Attrs(("tag", tag), ("reply", text ?? string.Empty)));
                return;
            }

            // Body click without a button: action "clicked".
            Fire("action", Attrs(("action", string.IsNullOrEmpty(action) ? "clicked" : action), ("tag", tag)));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Toast activation failed");
        }
    }

    private void Fire(string eventType, JsonObject data)
    {
        Logger.LogInformation("Notification {Event}: {Data}", eventType, data.ToJsonString());
        Hub.FireEvent("notification_action", eventType, data);
        var bus = (JsonObject)data.DeepClone();
        bus["type"] = eventType;
        Hub.FireBusEvent("notification_action", bus);
    }

    // ------------------------------------------------------------------ pairing code

    public void ShowPairingCode(PairingInfo pairing)
    {
        _pairing = pairing;
        try
        {
            var minutes = Math.Max(1, (int)Math.Round((pairing.Expires - DateTimeOffset.Now).TotalMinutes));
            var from = pairing.Address is null ? pairing.Client : $"{pairing.Client} ({pairing.Address})";
            var xml = new ToastContentBuilder()
                .AddArgument("system", "1")
                .AddText("Home Assistant pairing")
                .AddText($"Code: {pairing.Display}")
                .AddText($"Requested by {from}. Valid for {minutes} min.")
                .AddButton(new ToastButton().SetContent("Copy code").AddArgument("action", "copy_pairing_code").SetBackgroundActivation())
                .AddButton(new ToastButtonDismiss())
                .SetToastScenario(ToastScenario.Reminder)
                .GetToastContent()
                .GetXml();
            var toast = new ToastNotification(xml)
            {
                Tag = PairingTag,
                Group = SystemGroup,
                ExpirationTime = pairing.Expires,
            };
            _context.Ui.Post(() => ToastNotificationManagerCompat.CreateToastNotifier().Show(toast));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not show the pairing code");
        }
    }

    public void ClearPairingCode()
    {
        _pairing = null;
        try
        {
            ToastNotificationManagerCompat.History.Remove(PairingTag, SystemGroup);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not remove the pairing toast");
        }
    }

    /// <summary>Simple informational toast (used by the tray app).</summary>
    public void ShowInfo(string title, string text)
    {
        try
        {
            var xml = new ToastContentBuilder()
                .AddArgument("system", "1")
                .AddText(title)
                .AddText(text)
                .GetToastContent()
                .GetXml();
            var toast = new ToastNotification(xml) { Group = SystemGroup };
            _context.Ui.Post(() => ToastNotificationManagerCompat.CreateToastNotifier().Show(toast));
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not show a toast");
        }
    }

    // ------------------------------------------------------------------ images

    private static string SaveImage(byte[] data, string? contentType)
    {
        var extension = contentType switch
        {
            "image/png" => ".png",
            "image/gif" => ".gif",
            _ => ".jpg",
        };
        var name = Convert.ToHexString(SHA256.HashData(data))[..20].ToLowerInvariant() + extension;
        var path = Path.Combine(AgentPaths.Images, name);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(AgentPaths.Images);
            File.WriteAllBytes(path, data);
        }

        return path;
    }

    private void CleanupImages()
    {
        try
        {
            var files = new DirectoryInfo(AgentPaths.Images).GetFiles()
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(30);
            foreach (var file in files)
            {
                file.Delete();
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Image cleanup failed");
        }
    }
}
