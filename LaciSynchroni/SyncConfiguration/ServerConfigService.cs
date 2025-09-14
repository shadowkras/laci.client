using LaciSynchroni.SyncConfiguration.Configurations;

namespace LaciSynchroni.SyncConfiguration;

public class ServerConfigService : ConfigurationServiceBase<ServerConfig>
{
    public const string ConfigName = "server.json";

    public ServerConfigService(string configDir) : base(configDir)
    {
    }

    public override string ConfigurationName => ConfigName;
}