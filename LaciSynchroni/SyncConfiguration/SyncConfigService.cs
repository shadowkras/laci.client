using LaciSynchroni.SyncConfiguration.Configurations;

namespace LaciSynchroni.SyncConfiguration;

public class SyncConfigService : ConfigurationServiceBase<SyncConfig>
{
    public const string ConfigName = "config.json";

    public SyncConfigService(string configDir) : base(configDir)
    {
    }

    public override string ConfigurationName => ConfigName;
}