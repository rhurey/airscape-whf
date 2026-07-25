using System.Text.Json;

namespace AirscapeWhf.Automation;

/// <summary>
/// Holds the cooldown rules and persists them to disk.
/// <para>
/// If the path isn't writable — no volume mounted, wrong permissions — the rules still
/// work for the life of the process and the operator is told once. Losing the settings
/// on restart is a far better failure than refusing to start.
/// </para>
/// </summary>
public sealed class CooldownStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly ILogger<CooldownStore> _log;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private CooldownSettings _current = CooldownSettings.Disabled;
    private bool _warnedUnwritable;

    public CooldownStore(IConfiguration config, ILogger<CooldownStore> log)
    {
        _log = log;
        _path = config["Cooldown:Path"] ?? "/data/cooldown.json";
        Load();
    }

    public CooldownSettings Current => Volatile.Read(ref _current);

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                _log.LogInformation("No saved cooldown rules at {Path}; starting with none.", _path);
                return;
            }

            var settings = JsonSerializer.Deserialize<CooldownSettings>(File.ReadAllText(_path), Json);
            if (settings is null) return;

            Volatile.Write(ref _current, settings);
            _log.LogInformation("Loaded {Count} cooldown step(s) from {Path}, {State}.",
                settings.Steps.Count, _path, settings.Enabled ? "enabled" : "disabled");
        }
        catch (Exception ex)
        {
            // A corrupt settings file must not stop the fan being controllable.
            _log.LogWarning(ex, "Could not read cooldown rules from {Path}; starting with none.", _path);
        }
    }

    public async Task SaveAsync(CooldownSettings settings, CancellationToken ct = default)
    {
        Volatile.Write(ref _current, settings);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Write-and-rename, so a crash mid-write can't leave a half-file that
            // fails to load on the next start.
            var temp = _path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(settings, Json), ct)
                .ConfigureAwait(false);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            if (!_warnedUnwritable)
            {
                _warnedUnwritable = true;
                _log.LogWarning(ex,
                    "Cooldown rules are in effect but could not be saved to {Path}, so they will " +
                    "be lost on restart. Mount a writable volume there to keep them.", _path);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
