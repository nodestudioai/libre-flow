namespace LibreFlow.Services;

internal static class AppPaths
{
    /// <summary>LIBREFLOW_DATA points the app at a throwaway folder for testing.</summary>
    public static string DataDir { get; } =
        Environment.GetEnvironmentVariable("LIBREFLOW_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LibreFlow");

    public static bool IsCustomData { get; } = Environment.GetEnvironmentVariable("LIBREFLOW_DATA") is { Length: > 0 };

    public static string Database => Path.Combine(DataDir, "libreflow.db");
    public static string Settings => Path.Combine(DataDir, "settings.json");
    public static string AudioDir => Path.Combine(DataDir, "audio");
    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string WebViewData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LibreFlow", "WebView2");

    public static string AppDir => AppContext.BaseDirectory;
    public static string WebRoot => Path.Combine(AppDir, "wwwroot");
    public static string LexiconData => Path.Combine(AppDir, "Data");
    public static string Asset(string name) => Path.Combine(AppDir, "Assets", name);
}
