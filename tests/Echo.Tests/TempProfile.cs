using Echo.Core.Storage;

namespace Echo.Tests;

/// <summary>
/// An isolated %APPDATA% for the tests that exercise <see cref="SettingsStore"/>.
///
/// Those tests used to redirect the store by swapping the process-wide <c>APPDATA</c>
/// variable. That never worked: <see cref="Environment.GetFolderPath"/> asks Windows and
/// ignores the variable, so every run read and wrote the DEVELOPER'S OWN settings.json. It
/// went unnoticed while the tests only flipped two plausible values; on 2026-09-18 a new test
/// wrote "not json at all" into it and the real configuration was lost.
///
/// The redirection is now explicit (<c>StoragePaths(appDataRoot:)</c>) and <see cref="Paths"/>
/// still asserts the result lives inside the temp folder, so a future mistake fails the test
/// instead of touching a real profile.
/// </summary>
public sealed class TempProfile : IDisposable
{
    public TempProfile()
    {
        Root = Path.Combine(Path.GetTempPath(), "echo-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>The temp folder standing in for %APPDATA% and %LOCALAPPDATA%.</summary>
    public string Root { get; }

    /// <summary>A StoragePaths guaranteed to live inside <see cref="Root"/>.</summary>
    public StoragePaths Paths()
    {
        var p = new StoragePaths(appDataRoot: Root, localAppDataRoot: Root);
        if (!p.SettingsDir.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"TempProfile leaked: settings would be written to '{p.SettingsDir}', outside '{Root}'. "
                + "Refusing to touch a real profile from a test.");
        return p;
    }

    public SettingsStore Store() => new(Paths());

    public string SettingsFile => Paths().SettingsFile;

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}
