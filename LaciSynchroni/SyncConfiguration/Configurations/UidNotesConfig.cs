using LaciSynchroni.SyncConfiguration.Models;

namespace LaciSynchroni.SyncConfiguration.Configurations;

public class UidNotesConfig : ISyncConfiguration
{
    public Dictionary<string, ServerNotesStorage> ServerNotes { get; set; } = new(StringComparer.Ordinal);
    public int Version { get; set; } = 0;
}
