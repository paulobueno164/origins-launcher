namespace OriginsLauncher
{
    /// <summary>
    /// Portable startup never installs, relocates or registers the launcher. The game,
    /// settings and updater cache use Config.InstallDir (the executable's own folder).
    /// Old Installed Apps entries may still pass --uninstall after a manual replacement;
    /// this build must not delete a portable game or create an uninstaller process.
    /// </summary>
    internal static class PortableStartup
    {
        public static bool CanStart(Config config)
        {
            if (!config.Uninstall) return true;
            Log.Init(config);
            Log.Write("Portable launcher: --uninstall ignored. Close the game and remove its folder manually if desired.");
            return false;
        }
    }
}
