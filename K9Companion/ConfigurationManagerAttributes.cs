namespace K9Companion
{
    // Read by name by the F12 settings window (ConfigurationManager). Only the fields K9 sets are declared;
    // the window ignores the ones that are missing.
#pragma warning disable 0649
    internal sealed class ConfigurationManagerAttributes
    {
        // Hidden until "Advanced settings" is ticked at the top of the window.
        public bool? IsAdvanced;
        // Not shown in the window at all.
        public bool? Browsable;
    }
#pragma warning restore 0649
}
