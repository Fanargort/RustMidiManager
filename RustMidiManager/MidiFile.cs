using System.Buffers.Binary;

namespace RustMidiManager;

public sealed record MidiMessage(double Seconds, uint Data);
public sealed record MidiFile(int Format, int Tracks, double Duration, IReadOnlyList<MidiMessage> Messages)
{
    record Event(long Tick, uint Data, int Tempo = 0);

    public static MidiFile Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream);
        int U16() => BinaryPrimitives.ReadUInt16BigEndian(reader.ReadBytes(2));
        uint U32() => BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));
        long Chunk(string name)
        {
            if (!reader.ReadBytes(4).AsSpan().SequenceEqual(System.Text.Encoding.ASCII.GetBytes(name)))
                throw new InvalidDataException($"Ожидался блок {name}.");
            long end = stream.Position + U32() + 4;
            if (end > stream.Length) throw new InvalidDataException("Неполный блок MIDI.");
            return end;
        }

        var header = Chunk("MThd");
        if (header < 14) throw new InvalidDataException("Заголовок MIDI слишком короткий.");
        int format = U16(), tracks = U16(), division = U16();
        if (format > 1 || tracks == 0 || (format == 0 && tracks != 1) || division == 0)
            throw new InvalidDataException("Поддерживаются стандартные MIDI-файлы форматов 0 и 1.");
        stream.Position = header;
        var events = new List<Event>();
        long endTick = 0;

        for (int track = 0; track < tracks; track++)
        {
            long end = Chunk("MTrk"), tick = 0;
            int running = 0;
            bool finished = false;
            int Byte() => stream.Position < end ? reader.ReadByte() : throw new InvalidDataException("Дорожка MIDI оборвана.");
            int Variable()
            {
                int result = 0;
                for (int index = 0; index < 4; index++)
                {
                    int value = Byte();
                    result = (result << 7) | (value & 127);
                    if (value < 128) return result;
                }
                throw new InvalidDataException("Недопустимое число в дорожке MIDI.");
            }

            while (stream.Position < end)
            {
                tick += Variable();
                int status = Byte(), first = -1;
                if (status < 128)
                {
                    if (running == 0) throw new InvalidDataException("Неверный статус MIDI.");
                    first = status;
                    status = running;
                }
                if (status < 0xF0)
                {
                    running = status;
                    int type = status & 0xF0;
                    int data1 = first >= 0 ? first : Byte();
                    int data2 = type is 0xC0 or 0xD0 ? 0 : Byte();
                    if (data1 > 127 || data2 > 127) throw new InvalidDataException("Неверные данные MIDI.");
                    events.Add(new Event(tick, (uint)(status | data1 << 8 | data2 << 16)));
                    continue;
                }

                running = 0;
                if (status is not (0xFF or 0xF0 or 0xF7)) throw new InvalidDataException("Неизвестное событие MIDI.");
                int meta = status == 0xFF ? Byte() : -1;
                int length = Variable();
                long next = stream.Position + length;
                if (next > end) throw new InvalidDataException("Событие выходит за пределы дорожки.");
                if (meta == 0x51)
                {
                    if (length != 3) throw new InvalidDataException("Неверное событие темпа.");
                    int tempo = (Byte() << 16) | (Byte() << 8) | Byte();
                    if (tempo == 0) throw new InvalidDataException("Нулевой темп MIDI.");
                    events.Add(new Event(tick, 0, tempo));
                }
                if (meta == 0x2F)
                {
                    if (length != 0 || next != end) throw new InvalidDataException("Неверный конец дорожки MIDI.");
                    finished = true;
                }
                stream.Position = next;
            }
            if (!finished) throw new InvalidDataException("В дорожке отсутствует событие её окончания.");
            endTick = Math.Max(endTick, tick);
        }

        double? smpte = null;
        if ((division & 0x8000) != 0)
        {
            int frames = -(sbyte)(division >> 8), ticksPerFrame = division & 255;
            if (frames is not (24 or 25 or 29 or 30) || ticksPerFrame == 0)
                throw new InvalidDataException("Неизвестная частота SMPTE.");
            smpte = 1 / ((frames == 29 ? 30000.0 / 1001 : frames) * ticksPerFrame);
        }

        double seconds = 0;
        long previous = 0;
        int currentTempo = 500000;
        var messages = new List<MidiMessage>();
        foreach (var item in events.OrderBy(item => item.Tick))
        {
            seconds += (item.Tick - previous) * (smpte ?? currentTempo / (division * 1_000_000.0));
            previous = item.Tick;
            if (item.Tempo == 0) messages.Add(new MidiMessage(seconds, item.Data));
            else currentTempo = item.Tempo;
        }
        seconds += (endTick - previous) * (smpte ?? currentTempo / (division * 1_000_000.0));
        return new MidiFile(format, tracks, seconds, messages);
    }
}
