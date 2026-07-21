using System.Text.Json;
using System.Text.Json.Serialization;
using Drumless.Mobile.Core.Models;

namespace Drumless.Mobile.Core.Services;

public sealed class LibraryStateStore(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<LibraryState> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return new LibraryState();
        }

        try
        {
            await using var stream = File.OpenRead(filePath);
            return await JsonSerializer.DeserializeAsync<LibraryState>(
                       stream,
                       JsonOptions,
                       cancellationToken) ??
                   new LibraryState();
        }
        catch (JsonException)
        {
            return new LibraryState();
        }
        catch (IOException)
        {
            return new LibraryState();
        }
    }

    public async Task SaveAsync(
        LibraryState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{filePath}.tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
        }

        File.Move(temporaryPath, filePath, overwrite: true);
    }
}
