using Butlr.McpHome.Backends;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Butlr.McpHome.Tests;

public class ConsoleMockBackendTests
{
    private static ConsoleMockBackend NewBackend() =>
        new(NullLogger<ConsoleMockBackend>.Instance);

    [Fact]
    public async Task TurnOn_marks_room_as_on()
    {
        var backend = NewBackend();
        await backend.TurnOnLightAsync("salon");

        var states = backend.GetLightStates();
        Assert.True(states["salon"]);
    }

    [Fact]
    public async Task TurnOff_marks_room_as_off()
    {
        var backend = NewBackend();
        await backend.TurnOnLightAsync("salon");
        await backend.TurnOffLightAsync("salon");

        var states = backend.GetLightStates();
        Assert.False(states["salon"]);
    }

    [Fact]
    public async Task Multiple_rooms_are_tracked_independently()
    {
        var backend = NewBackend();
        await backend.TurnOnLightAsync("salon");
        await backend.TurnOffLightAsync("cuisine");

        var states = backend.GetLightStates();
        Assert.True(states["salon"]);
        Assert.False(states["cuisine"]);
    }

    [Fact]
    public async Task Room_lookup_is_case_insensitive()
    {
        var backend = NewBackend();
        await backend.TurnOnLightAsync("Salon");

        var states = backend.GetLightStates();
        // Le snapshot est lui aussi case-insensitive — "salon" == "SALON".
        Assert.True(states["salon"]);
        Assert.True(states["SALON"]);
    }

    [Fact]
    public async Task GetLightStates_returns_snapshot_not_live_view()
    {
        var backend = NewBackend();
        await backend.TurnOnLightAsync("salon");

        var snapshot = backend.GetLightStates();
        await backend.TurnOffLightAsync("salon");

        // Le snapshot reflète l'état au moment de l'appel, pas après.
        Assert.True(snapshot["salon"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TurnOn_rejects_blank_room(string room)
    {
        var backend = NewBackend();
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => backend.TurnOnLightAsync(room));
    }

    [Fact]
    public async Task TurnOn_rejects_null_room()
    {
        var backend = NewBackend();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => backend.TurnOnLightAsync(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TurnOff_rejects_blank_room(string room)
    {
        var backend = NewBackend();
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => backend.TurnOffLightAsync(room));
    }
}
