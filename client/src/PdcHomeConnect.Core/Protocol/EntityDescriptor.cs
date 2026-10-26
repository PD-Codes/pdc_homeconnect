using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PdcHomeConnect.Core.Protocol;

/// <summary>
/// Describes one Home Assistant entity. The integration creates entities purely from these
/// descriptors, so new features usually only need client changes.
/// </summary>
public sealed class EntityDescriptor
{
    /// <summary>Stable unique key (also used for the unique_id in Home Assistant).</summary>
    public required string Key { get; init; }

    /// <summary>Home Assistant platform, see <see cref="Platforms"/>.</summary>
    public required string Platform { get; init; }

    /// <summary>Translation key known to the integration (strings.json).</summary>
    public string? TranslationKey { get; init; }

    /// <summary>Values for placeholders in the translated name, e.g. {"disk": "C:"}.</summary>
    public Dictionary<string, string>? Placeholders { get; init; }

    /// <summary>English fallback name, used when the integration does not know the translation key.</summary>
    public string? Name { get; init; }

    public string? Icon { get; init; }

    public string? DeviceClass { get; init; }

    public string? StateClass { get; init; }

    public string? Unit { get; init; }

    /// <summary>Suggested display precision.</summary>
    public int? Precision { get; init; }

    public string? SuggestedUnit { get; init; }

    /// <summary>"config" or "diagnostic".</summary>
    public string? EntityCategory { get; init; }

    public bool EnabledByDefault { get; init; } = true;

    /// <summary>Options for enum sensors and selects.</summary>
    public List<string>? Options { get; init; }

    /// <summary>Event types for event entities.</summary>
    public List<string>? EventTypes { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    public double? Step { get; init; }

    /// <summary>Number mode: "auto", "box" or "slider".</summary>
    public string? Mode { get; init; }

    /// <summary>Supported features (media player).</summary>
    public List<string>? Features { get; init; }

    /// <summary>
    /// State reported while the owning component (e.g. the tray agent) is not connected.
    /// If null the entity becomes unavailable instead.
    /// </summary>
    public JsonNode? OfflineState { get; init; }

    /// <summary>Owner id inside the client (not sent to Home Assistant).</summary>
    [JsonIgnore]
    public string? Owner { get; set; }

    public EntityDescriptor With(Action<EntityDescriptorBuilder> configure)
    {
        var builder = new EntityDescriptorBuilder(this);
        configure(builder);
        return builder.Build();
    }
}

/// <summary>Small helper to derive a modified copy of a descriptor.</summary>
public sealed class EntityDescriptorBuilder(EntityDescriptor source)
{
    public string? Name { get; set; } = source.Name;
    public List<string>? Options { get; set; } = source.Options;
    public List<string>? EventTypes { get; set; } = source.EventTypes;
    public Dictionary<string, string>? Placeholders { get; set; } = source.Placeholders;
    public bool EnabledByDefault { get; set; } = source.EnabledByDefault;

    public EntityDescriptor Build() => new()
    {
        Key = source.Key,
        Platform = source.Platform,
        TranslationKey = source.TranslationKey,
        Placeholders = Placeholders,
        Name = Name,
        Icon = source.Icon,
        DeviceClass = source.DeviceClass,
        StateClass = source.StateClass,
        Unit = source.Unit,
        Precision = source.Precision,
        SuggestedUnit = source.SuggestedUnit,
        EntityCategory = source.EntityCategory,
        EnabledByDefault = EnabledByDefault,
        Options = Options,
        EventTypes = EventTypes,
        Min = source.Min,
        Max = source.Max,
        Step = source.Step,
        Mode = source.Mode,
        Features = source.Features,
        OfflineState = source.OfflineState?.DeepClone(),
        Owner = source.Owner,
    };
}
