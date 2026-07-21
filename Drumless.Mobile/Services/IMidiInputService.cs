using Drumless.Mobile.Core.Models;

namespace Drumless.Mobile.Services;

public interface IMidiInputService : IDisposable
{
    event EventHandler<MidiNoteEvent>? NoteReceived;
    event EventHandler<string>? StatusChanged;

    string Status { get; }
    Task StartAsync();
    void Stop();
}
