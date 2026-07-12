using BoxPilot.Core.Infrastructure;

namespace BoxPilot.Core.Tests;

public sealed class ConfigurationDraftStoreTests
{
    [Fact]
    public void SwitchToRestoresUnsavedConfiguration()
    {
        var store = new ConfigurationDraftStore();
        var firstProfile = Guid.NewGuid();
        var secondProfile = Guid.NewGuid();

        store.SwitchTo(firstProfile, "stored-first", string.Empty, false);
        store.SwitchTo(secondProfile, "stored-second", "draft-first", true);
        var restored = store.SwitchTo(firstProfile, "new-stored-first", "stored-second", false);

        Assert.Equal("draft-first", restored.Configuration);
        Assert.True(restored.IsDirty);
    }

    [Fact]
    public void MarkSavedDiscardsPreviousDraft()
    {
        var store = new ConfigurationDraftStore();
        var firstProfile = Guid.NewGuid();
        var secondProfile = Guid.NewGuid();

        store.SwitchTo(firstProfile, "stored-first", string.Empty, false);
        store.SwitchTo(secondProfile, "stored-second", "draft-first", true);
        store.MarkSaved(firstProfile);
        var restored = store.SwitchTo(firstProfile, "saved-first", "stored-second", false);

        Assert.Equal("saved-first", restored.Configuration);
        Assert.False(restored.IsDirty);
    }
}
