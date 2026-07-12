namespace BoxPilot.Core.Infrastructure;

public sealed class ConfigurationDraftStore
{
    private readonly Dictionary<Guid, string> drafts = [];

    public Guid? ActiveProfileId { get; private set; }

    public (string Configuration, bool IsDirty) SwitchTo(
        Guid profileId,
        string storedConfiguration,
        string currentConfiguration,
        bool currentIsDirty)
    {
        ArgumentNullException.ThrowIfNull(storedConfiguration);
        ArgumentNullException.ThrowIfNull(currentConfiguration);

        if (ActiveProfileId is { } currentProfileId && currentIsDirty)
            drafts[currentProfileId] = currentConfiguration;

        ActiveProfileId = profileId;
        return drafts.TryGetValue(profileId, out var draft)
            ? (draft, true)
            : (storedConfiguration, false);
    }

    public void MarkSaved(Guid profileId)
    {
        drafts.Remove(profileId);
        ActiveProfileId = profileId;
    }

    public void Remove(Guid profileId)
    {
        drafts.Remove(profileId);
        if (ActiveProfileId == profileId)
            ActiveProfileId = null;
    }
}
