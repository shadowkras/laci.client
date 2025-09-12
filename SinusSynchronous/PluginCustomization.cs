namespace SinusSynchronous
{
    /// <summary>
    /// A class that collects all the common compile-time customizations that people would like to do, for example renaming the plugin and the command
    /// the plugin listens to.
    /// </summary>
    public static class PluginCustomization
    {
        public static readonly string CommandName = "/laci";
        public static readonly string PluginName = "Laci Synchroni";
        public static readonly string PluginNameShort = "Laci";
    }
}