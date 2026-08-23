using NyKurEdge.Core.Events;
using NyKurEdge.Core.Settings;

namespace NyKurEdge.Core.Tests.Settings;

[TestClass]
public sealed class SettingsServiceTests
{
    [TestMethod]
    public async Task UpdateNormalizesAndPersistsValues()
    {
        var store = new MemorySettingsStore(new AppSettings());
        var service = new SettingsService(store, new EventBus());
        await service.InitializeAsync();

        await service.UpdateAsync(settings => settings with
        {
            Appearance = settings.Appearance with
            {
                ManualAccent = "not-a-color",
                EdgeThickness = 200,
            },
            Clock = settings.Clock with { IntervalMinutes = 4 },
        });

        Assert.AreEqual(24, service.Current.Appearance.EdgeThickness);
        Assert.AreEqual(15, service.Current.Clock.IntervalMinutes);
        Assert.AreEqual("#7286E8", service.Current.Appearance.ManualAccent);
        Assert.AreSame(service.Current, store.Saved);
    }

    private sealed class MemorySettingsStore(AppSettings initial) : ISettingsStore
    {
        public AppSettings? Saved { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(initial);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Saved = settings;
            return Task.CompletedTask;
        }
    }
}
