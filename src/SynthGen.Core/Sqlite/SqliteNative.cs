using System.Runtime.InteropServices;
using SQLitePCL;

namespace SynthGen.Sqlite;

/// <summary>
/// Wires Microsoft.Data.Sqlite to an explicitly provisioned native sqlite3 library.
/// No native binaries ship via NuGet. Windows enterprise setups use conda/micromamba;
/// Linux CI supplies the distro library through SYNTHGEN_SQLITE_DLL.
/// </summary>
public static class SqliteNative
{
    public const string DllEnvVar = "SYNTHGEN_SQLITE_DLL";

    private static readonly object Gate = new();
    private static bool _initialized;
    private static string? _loadedFrom;
    private static string? _failure;

    /// <summary>Path of the native library in use, once initialized.</summary>
    public static string? LoadedFrom => _loadedFrom;

    /// <summary>
    /// Locates and loads the explicitly provisioned sqlite3 library. Safe to call repeatedly.
    /// Returns false (with a reason) when no conda/micromamba sqlite is available.
    /// </summary>
    public static bool TryInitialize(out string? error)
    {
        lock (Gate)
        {
            if (_initialized) { error = null; return true; }
            if (_failure is not null) { error = _failure; return false; }

            var path = Locate();
            if (path is null)
            {
                _failure =
                    $"No native sqlite3 library found. Set {DllEnvVar} to the full path of the " +
                    "native SQLite library, or run scripts/setup-sqlite.ps1 for the Windows " +
                    "conda/micromamba setup.";
                error = _failure;
                return false;
            }

            try
            {
                SQLite3Provider_dynamic_cdecl.Setup("sqlite3", new NativeLibraryAdapter(path));
                raw.SetProvider(new SQLite3Provider_dynamic_cdecl());
                raw.FreezeProvider();
                _initialized = true;
                _loadedFrom = path;
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                _failure = $"Failed to load sqlite3 from '{path}': {ex.Message}";
                error = _failure;
                return false;
            }
        }
    }

    public static void Initialize()
    {
        if (!TryInitialize(out var error))
            throw new InvalidOperationException(error);
    }

    /// <summary>
    /// Probe order: explicit env var, the dedicated conda env from setup-sqlite.ps1,
    /// then any conda/micromamba env (base included) that carries sqlite3.dll. Linux
    /// callers should set SYNTHGEN_SQLITE_DLL because distro layouts are not probed.
    /// </summary>
    private static string? Locate()
    {
        var explicitPath = Environment.GetEnvironmentVariable(DllEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? explicitPath : null;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Path.Combine(home, "miniconda3"),
            Path.Combine(home, "anaconda3"),
            Path.Combine(home, "micromamba"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "micromamba"),
            Environment.GetEnvironmentVariable("CONDA_PREFIX") is { Length: > 0 } prefix
                ? Directory.GetParent(prefix)?.Parent?.FullName ?? prefix
                : null,
            Environment.GetEnvironmentVariable("MAMBA_ROOT_PREFIX"),
        };

        foreach (var root in roots.Where(r => r is not null && Directory.Exists(r)).Distinct())
        {
            var dedicated = Path.Combine(root!, "envs", "synthgen-sqlite", "Library", "bin", "sqlite3.dll");
            if (File.Exists(dedicated)) return dedicated;
        }

        foreach (var root in roots.Where(r => r is not null && Directory.Exists(r)).Distinct())
        {
            var baseDll = Path.Combine(root!, "Library", "bin", "sqlite3.dll");
            if (File.Exists(baseDll)) return baseDll;

            var envsDir = Path.Combine(root!, "envs");
            if (!Directory.Exists(envsDir)) continue;
            foreach (var env in Directory.EnumerateDirectories(envsDir))
            {
                var dll = Path.Combine(env, "Library", "bin", "sqlite3.dll");
                if (File.Exists(dll)) return dll;
            }
        }

        return null;
    }

    private sealed class NativeLibraryAdapter : IGetFunctionPointer
    {
        private readonly IntPtr _library;

        public NativeLibraryAdapter(string path) => _library = NativeLibrary.Load(path);

        public IntPtr GetFunctionPointer(string name) =>
            NativeLibrary.TryGetExport(_library, name, out var address) ? address : IntPtr.Zero;
    }
}
