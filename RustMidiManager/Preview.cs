using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace RustMidiManager;

sealed class Preview : IDisposable
{
    [DllImport("winmm.dll")] static extern uint midiOutOpen(out IntPtr handle, uint device, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] static extern uint midiOutShortMsg(IntPtr handle, uint message);
    [DllImport("winmm.dll")] static extern uint midiOutReset(IntPtr handle);
    [DllImport("winmm.dll")] static extern uint midiOutClose(IntPtr handle);

    readonly Stopwatch clock = new();
    readonly DispatcherTimer timer = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(10) };
    readonly int[] channelVolume = new int[16];
    MidiFile? midi;
    IntPtr handle;
    int index;
    double offset;
    public bool Playing => clock.IsRunning;
    public double Position => Math.Min(Duration, offset + clock.Elapsed.TotalSeconds);
    public double Duration => midi?.Duration ?? 0;
    public event Action? Changed;
    public int Volume { get; private set; } = 75;

    public Preview() => timer.Tick += (_, _) => Tick();

    public void SetVolume(int volume)
    {
        Volume = Math.Clamp(volume, 0, 100);
        if (handle == IntPtr.Zero) return;
        for (int channel = 0; channel < 16; channel++) SendVolume(channel);
    }

    public void Load(MidiFile file)
    {
        Stop();
        midi = file;
        Changed?.Invoke();
    }

    public void PlayOrPause()
    {
        if (midi == null) return;
        if (Playing)
        {
            offset = Position;
            clock.Reset();
            timer.Stop();
            Release();
            Changed?.Invoke();
            return;
        }
        if (offset >= Duration) Stop();
        uint result = midiOutOpen(out var device, uint.MaxValue, IntPtr.Zero, IntPtr.Zero, 0);
        if (result != 0) throw new IOException("Windows не смогла открыть MIDI-синтезатор. Код " + result + ".");
        handle = device;
        Array.Fill(channelVolume, 100);
        SetVolume(Volume);
        for (int position = 0; position < index; position++)
        {
            uint data = midi.Messages[position].Data;
            if ((data & 0xF0) >= 0xB0) Send(data);
        }
        clock.Start();
        timer.Start();
        Tick();
    }

    void SendVolume(int channel) =>
        midiOutShortMsg(handle, (uint)((0xB0 | channel) | (7 << 8) | ((channelVolume[channel] * Volume / 100) << 16)));

    void Send(uint data)
    {
        if ((data & 0xFFF0) == 0x07B0)
        {
            int channel = (int)(data & 15);
            channelVolume[channel] = (int)(data >> 16);
            SendVolume(channel);
        }
        else midiOutShortMsg(handle, data);
    }

    void Tick()
    {
        if (midi == null) return;
        double position = Position;
        while (index < midi.Messages.Count && midi.Messages[index].Seconds <= position)
            Send(midi.Messages[index++].Data);
        if (position >= Duration)
        {
            offset = Duration;
            clock.Reset();
            timer.Stop();
            Release();
        }
        Changed?.Invoke();
    }

    void Release()
    {
        if (handle == IntPtr.Zero) return;
        midiOutReset(handle);
        midiOutClose(handle);
        handle = IntPtr.Zero;
    }

    public void Stop()
    {
        timer.Stop();
        clock.Reset();
        offset = 0;
        index = 0;
        Release();
        Changed?.Invoke();
    }

    public void Dispose() => Stop();
}
