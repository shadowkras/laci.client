namespace LaciSynchroni.SyncConfiguration.Models;

public class ServerNotesStorage
{
    public Dictionary<string, string> GidServerComments { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> UidServerComments { get; set; } = new(StringComparer.Ordinal);
}