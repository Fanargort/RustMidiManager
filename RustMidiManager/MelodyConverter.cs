using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Dsp;

namespace RustMidiManager;

internal sealed record MelodyNote(int Pitch, int Start, int End);

internal static class MelodyConverter
{
    const int Rate = 11025;
    const int Window = 4096;
    const int Hop = 220;
    const int MaxSeconds = 600;

    public static byte[] Convert(string path, int minimumPitch, CancellationToken cancellation, IProgress<int> progress)
    {
        using var input = new AudioFileReader(path);
        if (input.TotalTime.TotalSeconds > MaxSeconds)
            throw new InvalidDataException("Выберите запись длительностью до 10 минут.");
        ISampleProvider samples = input;
        if (samples.WaveFormat.Channels == 2)
            samples = new StereoToMonoSampleProvider(samples);
        if (samples.WaveFormat.Channels != 1)
            throw new InvalidDataException("Поддерживаются моно и стерео записи.");
        samples = new WdlResamplingSampleProvider(samples, Rate);
        var audio = new List<float>();
        var buffer = new float[Rate];
        int read;
        while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            if (audio.Count + read > Rate * MaxSeconds)
                throw new InvalidDataException("Выберите запись длительностью до 10 минут.");
            audio.AddRange(buffer.Take(read));
        }
        progress.Report(10);
        return Encode(Analyze(audio.ToArray(), minimumPitch, cancellation, progress));
    }

    internal static List<MelodyNote> Analyze(float[] audio, int minimumPitch, CancellationToken cancellation, IProgress<int>? progress = null)
    {
        if (minimumPitch < 36 || minimumPitch > 72) throw new ArgumentOutOfRangeException(nameof(minimumPitch));
        var pitches = new List<int>();
        var fft = new Complex[Window];
        var spectrum = new double[Window / 2];
        double peak = audio.Length == 0 ? 0 : audio.Max(sample => Math.Abs(sample));
        for (int start = 0; start < audio.Length; start += Hop)
        {
            cancellation.ThrowIfCancellationRequested();
            double energy = 0;
            for (int i = 0; i < Window; i++)
            {
                int position = start + i - Window / 2;
                float sample = position >= 0 && position < audio.Length ? audio[position] : 0;
                energy += sample * sample;
                fft[i].X = (float)(sample * .5 * (1 - Math.Cos(2 * Math.PI * i / (Window - 1))));
                fft[i].Y = 0;
            }
            int pitch = -1;
            if (Math.Sqrt(energy / Window) > Math.Max(.001, peak * .015))
            {
                FastFourierTransform.FFT(true, 12, fft);
                for (int i = 0; i < spectrum.Length; i++)
                    spectrum[i] = Math.Sqrt(fft[i].X * fft[i].X + fft[i].Y * fft[i].Y);
                double best = 0;
                for (int note = minimumPitch; note <= minimumPitch + 36; note++)
                {
                    double bin = 440 * Math.Pow(2, (note - 69) / 12.0) * Window / Rate;
                    double strength = Magnitude(spectrum, bin);
                    double score = strength;
                    for (int harmonic = 2; harmonic <= 4; harmonic++)
                        score += Magnitude(spectrum, bin * harmonic) / (harmonic * 2);
                    // Require a fundamental to avoid interpreting harmonics as missing lower notes.
                    score *= strength / (strength + .002);
                    if (score > best) { best = score; pitch = note; }
                }
            }
            pitches.Add(pitch);
            if (pitches.Count % 50 == 0) progress?.Report(10 + (int)(85L * start / Math.Max(1, audio.Length)));
        }
        var smooth = pitches.ToArray();
        for (int i = 1; i + 1 < pitches.Count; i++)
            if (pitches[i - 1] == pitches[i + 1]) smooth[i] = pitches[i - 1];
        var notes = new List<MelodyNote>();
        int current = -1, began = 0;
        int duration = (int)Math.Ceiling(audio.Length * 960.0 / Rate);
        for (int i = 0; i <= smooth.Length; i++)
        {
            int pitch = i < smooth.Length ? smooth[i] : -1;
            if (pitch == current) continue;
            int tick = Math.Min(duration, (int)Math.Round(i * Hop * 960.0 / Rate));
            if (current >= 0 && tick - began >= 77) notes.Add(new MelodyNote(current, began, tick));
            current = pitch;
            began = tick;
        }
        if (notes.Count == 0) throw new InvalidDataException("Устойчивая мелодия не найдена. Попробуйте запись с более отчётливым голосом или сольным инструментом.");
        return notes;
    }

    static double Magnitude(double[] spectrum, double bin)
    {
        int center = (int)Math.Round(bin);
        double result = 0;
        for (int i = Math.Max(1, center - 1); i <= Math.Min(spectrum.Length - 1, center + 1); i++)
            result = Math.Max(result, spectrum[i]);
        return result;
    }

    internal static byte[] Encode(IEnumerable<MelodyNote> notes)
    {
        using var track = new MemoryStream();
        void Variable(int value)
        {
            if (value < 0) throw new InvalidDataException("Неверная последовательность нот.");
            Span<byte> bytes = stackalloc byte[4];
            int index = 3;
            bytes[index] = (byte)(value & 127);
            while ((value >>= 7) > 0) bytes[--index] = (byte)((value & 127) | 128);
            track.Write(bytes[index..]);
        }
        track.Write(new byte[] { 0, 0xFF, 0x51, 3, 7, 0xA1, 0x20, 0, 0xC0, 0 });
        int previous = 0;
        foreach (var note in notes)
        {
            Variable(note.Start - previous);
            track.Write(new byte[] { 0x90, (byte)note.Pitch, 90 });
            Variable(note.End - note.Start);
            track.Write(new byte[] { 0x80, (byte)note.Pitch, 0 });
            previous = note.End;
        }
        track.Write(new byte[] { 0, 0xFF, 0x2F, 0 });
        using var output = new MemoryStream();
        output.Write(ConvertHex("4D546864000000060000000101E04D54726B"));
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, (int)track.Length);
        output.Write(length);
        track.Position = 0;
        track.CopyTo(output);
        return output.ToArray();
    }

    static byte[] ConvertHex(string value) => System.Convert.FromHexString(value);
}
