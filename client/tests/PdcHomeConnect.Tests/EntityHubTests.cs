using System.Text.Json.Nodes;
using PdcHomeConnect.Core.Hub;
using PdcHomeConnect.Core.Protocol;

namespace PdcHomeConnect.Tests;

public class EntityHubTests
{
    private static EntityDescriptor Sensor(string key) =>
        new() { Key = key, Platform = Platforms.Sensor, Name = key, Unit = "%" };

    [Fact]
    public void Descriptor_changes_bump_revision_only_when_different()
    {
        var hub = new EntityHub(Log.For<EntityHub>());
        hub.SetDescriptors("a", [Sensor("cpu"), Sensor("ram")]);
        Assert.Equal(1, hub.Revision);
        hub.SetDescriptors("a", [Sensor("cpu"), Sensor("ram")]);
        Assert.Equal(1, hub.Revision);
        hub.SetDescriptors("a", [Sensor("cpu")]);
        Assert.Equal(2, hub.Revision);
        Assert.Single(hub.GetDescriptors());
    }

    [Fact]
    public void Conflicting_keys_from_other_owner_are_ignored()
    {
        var hub = new EntityHub(Log.For<EntityHub>());
        hub.SetDescriptors("a", [Sensor("cpu")]);
        hub.SetDescriptors("b", [Sensor("cpu"), Sensor("gpu")]);
        Assert.Equal(2, hub.GetDescriptors().Count);
        Assert.Single(hub.GetDescriptors("b"));
    }

    [Fact]
    public void Identical_states_are_suppressed_and_drain_orders_messages()
    {
        var hub = new EntityHub(Log.For<EntityHub>());
        hub.SetDescriptors("a", [Sensor("cpu")]);
        hub.SetState("cpu", JsonValue.Create(5));
        hub.FireEvent("cpu", "spike");
        var changes = hub.DrainChanges();
        Assert.NotNull(changes.Entities);
        Assert.NotNull(changes.States);
        Assert.Single(changes.Messages);

        hub.SetState("cpu", JsonValue.Create(5));
        Assert.True(hub.DrainChanges().IsEmpty);

        hub.SetState("cpu", JsonValue.Create(6));
        var next = hub.DrainChanges();
        Assert.Null(next.Entities);
        Assert.Equal(6, next.States!["states"]!["cpu"]!["state"]!.GetValue<int>());
    }

    [Fact]
    public void State_published_before_descriptor_is_resent()
    {
        var hub = new EntityHub(Log.For<EntityHub>());
        hub.SetState("late", JsonValue.Create("x"));
        var first = hub.DrainChanges();
        Assert.Null(first.States);
        hub.SetDescriptors("a", [Sensor("late")]);
        var second = hub.DrainChanges();
        Assert.NotNull(second.States);
        Assert.Equal("x", second.States!["states"]!["late"]!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Commands_route_to_owner_and_fallback()
    {
        var hub = new EntityHub(Log.For<EntityHub>());
        hub.SetDescriptors("svc", [Sensor("a")]);
        hub.SetDescriptors("agent", [Sensor("b")]);
        hub.RegisterCommandHandler("svc", new EchoHandler());
        hub.FallbackCommand = (owner, cmd, ct) => Task.FromResult(CommandResult.Ok(JsonValue.Create(owner)));

        var a = await hub.ExecuteCommandAsync(new EntityCommand("a", "press", []), CancellationToken.None);
        Assert.True(a.Success);
        Assert.Equal("a", a.Data!.GetValue<string>());

        var b = await hub.ExecuteCommandAsync(new EntityCommand("b", "press", []), CancellationToken.None);
        Assert.Equal("agent", b.Data!.GetValue<string>());

        var missing = await hub.ExecuteCommandAsync(new EntityCommand("zzz", "press", []), CancellationToken.None);
        Assert.False(missing.Success);
        Assert.Equal(ErrorCodes.NotFound, missing.ErrorCode);
    }

    [Fact]
    public async Task Unknown_methods_use_fallback_with_method_name()
    {
        var hub = new EntityHub(Log.For<EntityHub>());
        hub.FallbackMethod = (p, ct) => Task.FromResult(CommandResult.Ok(p["__method"]!.DeepClone()));
        var result = await hub.CallMethodAsync("notify", new JsonObject { ["message"] = "hi" }, CancellationToken.None);
        Assert.Equal("notify", result.Data!.GetValue<string>());
    }

    private sealed class EchoHandler : ICommandHandler
    {
        public Task<CommandResult> HandleCommandAsync(EntityCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(CommandResult.Ok(JsonValue.Create(command.Key)));
    }
}
