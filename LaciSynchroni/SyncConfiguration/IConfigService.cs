using LaciSynchroni.SyncConfiguration.Configurations;

namespace LaciSynchroni.SyncConfiguration;

public interface IConfigService<out T> : IDisposable where T : ISyncConfiguration
{
    T Current { get; }
    string ConfigurationName { get; }
    string ConfigurationPath { get; }
    public event EventHandler? ConfigSave;
    void UpdateLastWriteTime();
}
