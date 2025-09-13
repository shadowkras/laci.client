using LaciSynchroni.SyncConfiguration.Models;

namespace LaciSynchroni.SyncConfiguration.Configurations;

public class ServerTagConfig : ISyncConfiguration
{
    public Dictionary<string, ServerTagStorage> ServerTagStorage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ServerTagStorage GlobalTagStorage { get; set; } = new();
    public int Version { get; set; } = 0;
}