using Drumless.Mobile.Core.Models;
using Drumless.Mobile.Services;

namespace Drumless.Mobile;

public sealed class PlatformMidiInputService : IMidiInputService
{
#pragma warning disable CS0067
    public event EventHandler<MidiNoteEvent>? NoteReceived;
#pragma warning restore CS0067
    public event EventHandler<string>? StatusChanged;

    public string Status { get; private set; } =
        "Conecta una batería MIDI y vuelve a abrir la aplicación";

    public Task StartAsync()
    {
        StatusChanged?.Invoke(this, Status);
        return Task.CompletedTask;
    }

    public void Stop()
    {
    }

    public void Dispose() => Stop();
}
