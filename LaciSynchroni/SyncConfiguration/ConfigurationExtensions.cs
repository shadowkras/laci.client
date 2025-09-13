using LaciSynchroni.SyncConfiguration.Configurations;

namespace LaciSynchroni.SyncConfiguration;

public static class ConfigurationExtensions
{
    public static bool HasValidSetup(this SyncConfig configuration)
    {
        return configuration.AcceptedAgreement && configuration.InitialScanComplete
                    && !string.IsNullOrEmpty(configuration.CacheFolder)
                    && Directory.Exists(configuration.CacheFolder);
    }
}