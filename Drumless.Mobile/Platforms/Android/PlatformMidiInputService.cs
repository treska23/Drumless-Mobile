using Android.Content;
using Android.Media.Midi;
using Android.OS;
using Drumless.Mobile.Core.Models;
using Drumless.Mobile.Services;

namespace Drumless.Mobile;

public sealed class PlatformMidiInputService : IMidiInputService
{
    private readonly MidiManager? _manager;
    private MidiDevice? _device;
    private MidiOutputPort? _port;
    private NoteReceiver? _receiver;

    public PlatformMidiInputService()
    {
        _manager = Android.App.Application.Context.GetSystemService(Context.MidiService)
            as MidiManager;
    }

    public event EventHandler<MidiNoteEvent>? NoteReceived;
    public event EventHandler<string>? StatusChanged;

    public string Status { get; private set; } = "Buscando batería MIDI…";

    public async Task StartAsync()
    {
        Stop();
        if (_manager is null)
        {
            SetStatus("Este móvil no ofrece el servicio MIDI de Android");
            return;
        }

        IEnumerable<MidiDeviceInfo> devices;
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            devices = _manager.GetDevicesForTransport(
                (int)MidiTransport.MidiByteStream) ?? [];
        }
        else
        {
#pragma warning disable CS0618
            devices = _manager.GetDevices() ?? [];
#pragma warning restore CS0618
        }
        var candidate = devices.FirstOrDefault(device =>
                            device.Type == MidiDeviceType.Usb &&
                            device.OutputPortCount > 0)
                        ?? devices.FirstOrDefault(device => device.OutputPortCount > 0);
        if (candidate is null)
        {
            SetStatus("Sin batería MIDI · conecta el USB y pulsa Reconectar");
            return;
        }

        var completion = new TaskCompletionSource<MidiDevice?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new DeviceOpenedListener(completion);
        _manager.OpenDevice(candidate, listener, new Handler(Looper.MainLooper!));
        _device = await completion.Task.WaitAsync(TimeSpan.FromSeconds(8));
        if (_device is null)
        {
            SetStatus("Android no pudo abrir el dispositivo MIDI");
            return;
        }

        _port = _device.OpenOutputPort(0);
        if (_port is null)
        {
            SetStatus("La batería no ofrece un puerto MIDI de salida");
            Stop();
            return;
        }

        _receiver = new NoteReceiver(note =>
            NoteReceived?.Invoke(this, note));
        _port.Connect(_receiver);
        var name = candidate.Properties?.GetString(MidiDeviceInfo.PropertyName);
        SetStatus($"MIDI conectado · {name ?? "batería USB"}");
    }

    public void Stop()
    {
        if (_port is not null && _receiver is not null)
        {
            _port.Disconnect(_receiver);
        }

        _receiver?.Dispose();
        _receiver = null;
        _port?.Close();
        _port?.Dispose();
        _port = null;
        _device?.Close();
        _device?.Dispose();
        _device = null;
    }

    public void Dispose() => Stop();

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    private sealed class DeviceOpenedListener(
        TaskCompletionSource<MidiDevice?> completion)
        : Java.Lang.Object, MidiManager.IOnDeviceOpenedListener
    {
        public void OnDeviceOpened(MidiDevice? device) =>
            completion.TrySetResult(device);
    }

    private sealed class NoteReceiver(Action<MidiNoteEvent> onNote) : MidiReceiver
    {
        public override void OnSend(
            byte[]? message,
            int offset,
            int count,
            long timestamp)
        {
            if (message is null || count < 3)
            {
                return;
            }

            var end = Math.Min(message.Length, offset + count);
            for (var index = offset; index + 2 < end;)
            {
                var status = message[index];
                var command = status & 0xF0;
                if (command is 0x90 or 0x80)
                {
                    var note = message[index + 1] & 0x7F;
                    var velocity = command == 0x80
                        ? 0
                        : message[index + 2] & 0x7F;
                    if (velocity > 0)
                    {
                        onNote(new MidiNoteEvent(
                            note,
                            velocity,
                            timestamp > 0
                                ? timestamp
                                : SystemClock.ElapsedRealtimeNanos()));
                    }
                    index += 3;
                    continue;
                }

                index += command is 0xC0 or 0xD0 ? 2 : 3;
            }
        }
    }
}
