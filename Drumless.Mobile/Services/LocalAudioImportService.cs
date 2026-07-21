namespace Drumless.Mobile.Services;

public sealed record ImportedAudio(string Title, string Path, string OriginalFileName);

public sealed class LocalAudioImportService
{
    private static readonly HashSet<string> KnownExtensions =
    [
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".aiff"
    ];

    public async Task<IReadOnlyList<ImportedAudio>> PickAndImportAsync()
    {
        var audioFiles = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            [DevicePlatform.Android] = ["audio/*"],
            [DevicePlatform.iOS] = ["public.audio", "public.mp3", "com.microsoft.waveform-audio"]
        });
        var results = await FilePicker.Default.PickMultipleAsync(new PickOptions
        {
            PickerTitle = "Selecciona pistas de audio",
            FileTypes = audioFiles
        });

        var destinationFolder = Path.Combine(FileSystem.AppDataDirectory, "audio");
        Directory.CreateDirectory(destinationFolder);
        var imported = new List<ImportedAudio>();

        foreach (var result in results)
        {
            if (result is null)
            {
                continue;
            }

            var originalName = string.IsNullOrWhiteSpace(result.FileName)
                ? "Pista de audio"
                : result.FileName;
            var extension = Path.GetExtension(originalName).ToLowerInvariant();
            if (!KnownExtensions.Contains(extension))
            {
                extension = ".audio";
            }

            var destination = Path.Combine(
                destinationFolder,
                $"{Guid.NewGuid():N}{extension}");
            try
            {
                await using var source = await result.OpenReadAsync();
                await using var target = File.Create(destination);
                await source.CopyToAsync(target);

                var title = Path.GetFileNameWithoutExtension(originalName);
                imported.Add(new ImportedAudio(
                    string.IsNullOrWhiteSpace(title) ? "Pista local" : title,
                    destination,
                    originalName));
            }
            catch
            {
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }

                throw;
            }
        }

        return imported;
    }
}
