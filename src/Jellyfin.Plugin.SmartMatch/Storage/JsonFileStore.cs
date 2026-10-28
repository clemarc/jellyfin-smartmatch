using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartMatch.Storage;

/// <summary>
/// Versioned envelope written to every journal file.
/// </summary>
/// <typeparam name="T">Payload type.</typeparam>
public sealed class Envelope<T>
{
    /// <summary>Gets or sets the schema version.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>Gets or sets the time of the write.</summary>
    public DateTime WrittenUtc { get; set; }

    /// <summary>Gets or sets the payload.</summary>
    public T? Data { get; set; }
}

/// <summary>
/// Crash-safe JSON file IO: write to a temp file, fsync, keep the previous file as .bak, then rename.
/// A truncated or corrupt file falls back to the backup; if both are unreadable the corrupt file is
/// quarantined and the store starts empty instead of failing Jellyfin startup.
/// </summary>
public sealed class JsonFileStore : IDisposable
{
    /// <summary>Current schema version of all journal files.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Gets the serializer options used for journal files.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _directory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonFileStore"/> class.
    /// </summary>
    /// <param name="directory">Data directory (created if missing).</param>
    /// <param name="logger">Logger.</param>
    public JsonFileStore(string directory, ILogger logger)
    {
        _directory = directory;
        _logger = logger;
        Directory.CreateDirectory(directory);
    }

    /// <inheritdoc />
    public void Dispose() => _writeLock.Dispose();

    /// <summary>Gets the data directory.</summary>
    public string DirectoryPath => _directory;

    /// <summary>
    /// Loads a file.
    /// </summary>
    /// <typeparam name="T">Payload type.</typeparam>
    /// <param name="name">File name.</param>
    /// <param name="warning">Set when the primary file was unreadable.</param>
    /// <returns>The payload or null when no readable file exists.</returns>
    public T? Load<T>(string name, out string? warning)
        where T : class
    {
        warning = null;
        var path = Path.Combine(_directory, name);
        var backup = path + ".bak";

        if (TryRead<T>(path, out var data, out var error))
        {
            return data;
        }

        if (error is null)
        {
            // Missing file: first run.
            return null;
        }

        _logger.LogError("Smart Match journal file {File} is unreadable: {Error}", name, error);
        if (TryRead<T>(backup, out var backupData, out var backupError) && backupData is not null)
        {
            warning = $"{name} was unreadable and was restored from its backup.";
            return backupData;
        }

        var quarantine = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        try
        {
            File.Move(path, quarantine, overwrite: true);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Could not quarantine corrupt journal file {File}", name);
        }

        warning = $"{name} and its backup were unreadable ({backupError ?? error}); the file was quarantined and an empty journal started.";
        return null;
    }

    /// <summary>
    /// Atomically writes a payload.
    /// </summary>
    /// <param name="name">File name.</param>
    /// <param name="payload">UTF-8 JSON bytes of the envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    public async Task WriteAsync(string name, byte[] payload, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, name);
        var temp = path + ".tmp";
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Serializes an envelope.
    /// </summary>
    /// <typeparam name="T">Payload type.</typeparam>
    /// <param name="data">Payload.</param>
    /// <returns>UTF-8 JSON.</returns>
    public static byte[] Serialize<T>(T data)
        => JsonSerializer.SerializeToUtf8Bytes(new Envelope<T> { SchemaVersion = SchemaVersion, WrittenUtc = DateTime.UtcNow, Data = data }, Options);

    private static bool TryRead<T>(string path, out T? data, out string? error)
        where T : class
    {
        data = null;
        error = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            var envelope = JsonSerializer.Deserialize<Envelope<T>>(bytes, Options);
            if (envelope is null)
            {
                error = "empty document";
                return false;
            }

            if (envelope.SchemaVersion > SchemaVersion)
            {
                error = string.Create(CultureInfo.InvariantCulture, $"schema version {envelope.SchemaVersion} is newer than supported {SchemaVersion} (downgrade)");
                return false;
            }

            data = envelope.Data;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
