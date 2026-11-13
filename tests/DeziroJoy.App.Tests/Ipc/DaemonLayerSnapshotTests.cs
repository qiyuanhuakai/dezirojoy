using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Ipc;

/// <summary>
/// The temporary layer reaches the GUI through the daemon's snapshot: the active layer of
/// every connected device, with the mappings that layer puts in effect. The daemon itself
/// owns no engine, so the wiring that owns both the engine host and this server points
/// <see cref="DaemonServer.LayerStateSource"/> at the host's layer state; a server left
/// unwired reports no layer, exactly as a device running its base mappings alone does.
/// </summary>
public class DaemonLayerSnapshotTests
{
    private static DaemonServer CreateServer(FakeDaemonHost host, string statePath)
        => new(host, new InMemoryProfileStore(), new DaemonStateStore(statePath), new FakeClock());

    [Fact]
    public async Task The_Snapshot_Carries_The_Active_Layer_And_Its_Effective_Mappings()
    {
        var temp = new TempStateFile();
        var server = CreateServer(new FakeDaemonHost(), temp.Path);

        var dragId = Guid.NewGuid();
        var layerXId = Guid.NewGuid();
        server.LayerStateSource = () =>
        [
            new ActiveLayerSnapshot(
                "xinput:0",
                2,
                [
                    new EffectiveMappingSnapshot(dragId, 0),
                    new EffectiveMappingSnapshot(layerXId, 2),
                ]),
        ];

        var response = await server.ProcessAsync(new GetStateMessage("state-1"));
        var state = Assert.IsType<StateChangedMessage>(response);

        var layer = Assert.Single(state.Snapshot.Layers);
        Assert.Equal("xinput:0", layer.DeviceId);
        Assert.Equal(2, layer.LayerId);
        Assert.Contains(layer.Mappings, m => m.MappingId == dragId && m.Layer == 0);
        Assert.Contains(layer.Mappings, m => m.MappingId == layerXId && m.Layer == 2);
        Assert.Equal("state-1", state.RequestId);
    }

    [Fact]
    public async Task A_Server_With_No_Layer_Source_Reports_No_Layer()
    {
        var temp = new TempStateFile();
        var server = CreateServer(new FakeDaemonHost(), temp.Path);

        var response = await server.ProcessAsync(new GetStateMessage("state-2"));
        var state = Assert.IsType<StateChangedMessage>(response);

        Assert.Empty(state.Snapshot.Layers);
    }

    [Fact]
    public void The_Snapshot_Serializes_The_Layers_It_Carries()
    {
        var snapshot = new StateSnapshot(
            1,
            1,
            Running: true,
            MappingEnabled: true,
            InjectorPid: 4242,
            InjectorIntegrity: "high",
            ProfileName: "layer",
            LastError: null,
            InjectedEvents: 0,
            BlockedEvents: 0,
            HasController: true)
        {
            Layers = [new ActiveLayerSnapshot("xinput:0", 2, [new EffectiveMappingSnapshot(Guid.NewGuid(), 2)])],
        };

        var json = IpcSerializer.Serialize(new StateChangedMessage("daemon-1", snapshot));
        var back = Assert.IsType<StateChangedMessage>(IpcSerializer.Deserialize(json));

        var layer = Assert.Single(back.Snapshot.Layers);
        Assert.Equal(2, layer.LayerId);
        Assert.Equal(2, Assert.Single(layer.Mappings).Layer);
    }

    private sealed class TempStateFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"dezirojoy-layer-{Guid.NewGuid():n}.json");

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
                // A leftover temp file is not worth failing a test over.
            }
        }
    }
}
