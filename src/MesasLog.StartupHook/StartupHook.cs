using System.IO;
using System.Reflection;
using System.Runtime.Loader;

// Startup hook: tipo no namespace global, conforme docs do runtime (.NET).
internal static class StartupHook
{
    public static void Initialize()
    {
        AssemblyLoadContext.Default.Resolving += static (_, an) =>
        {
            if (string.IsNullOrEmpty(an.Name)) return null;
            var simple = an.Name.Split(',')[0];
            var path = Path.Combine(AppContext.BaseDirectory, "assemblies", simple + ".dll");
            return File.Exists(path)
                ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path)
                : null;
        };
    }
}
