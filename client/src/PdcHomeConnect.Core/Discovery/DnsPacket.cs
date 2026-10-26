using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace PdcHomeConnect.Core.Discovery;

/// <summary>DNS record types used by the mDNS responder.</summary>
public static class DnsType
{
    public const ushort A = 1;
    public const ushort Ptr = 12;
    public const ushort Txt = 16;
    public const ushort Aaaa = 28;
    public const ushort Srv = 33;
    public const ushort Nsec = 47;
    public const ushort Any = 255;
}

/// <summary>A question parsed from a DNS message.</summary>
public sealed record DnsQuestion(string Name, ushort Type, ushort Class, bool UnicastResponse);

/// <summary>Parsed DNS message header and questions (answers are not needed by the responder).</summary>
public sealed record DnsQuery(ushort Id, ushort Flags, IReadOnlyList<DnsQuestion> Questions)
{
    public bool IsResponse => (Flags & 0x8000) != 0;
}

/// <summary>Minimal DNS wire format reader.</summary>
public static class DnsReader
{
    public static DnsQuery? TryParse(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length < 12)
            {
                return null;
            }

            var id = BinaryPrimitives.ReadUInt16BigEndian(data);
            var flags = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
            var questionCount = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
            var offset = 12;
            var questions = new List<DnsQuestion>(questionCount);
            for (var i = 0; i < questionCount; i++)
            {
                var name = ReadName(data, ref offset);
                if (offset + 4 > data.Length)
                {
                    return null;
                }

                var type = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
                var cls = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
                offset += 4;
                questions.Add(new DnsQuestion(name, type, (ushort)(cls & 0x7FFF), (cls & 0x8000) != 0));
            }

            return new DnsQuery(id, flags, questions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Reads a (possibly compressed) domain name.</summary>
    public static string ReadName(ReadOnlySpan<byte> data, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        var guard = 0;
        while (true)
        {
            if (position >= data.Length || ++guard > 128)
            {
                throw new FormatException("Invalid DNS name.");
            }

            var length = data[position];
            if (length == 0)
            {
                position++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= data.Length)
                {
                    throw new FormatException("Invalid DNS pointer.");
                }

                var pointer = ((length & 0x3F) << 8) | data[position + 1];
                if (!jumped)
                {
                    offset = position + 2;
                }

                jumped = true;
                position = pointer;
                continue;
            }

            position++;
            if (position + length > data.Length)
            {
                throw new FormatException("Invalid DNS label.");
            }

            labels.Add(Encoding.UTF8.GetString(data.Slice(position, length)));
            position += length;
        }

        if (!jumped)
        {
            offset = position;
        }

        return string.Join('.', labels);
    }
}

/// <summary>Minimal DNS wire format writer (no name compression).</summary>
public sealed class DnsWriter
{
    private readonly List<byte> _buffer = new(512);

    public void Header(ushort id, ushort flags, ushort questions, ushort answers, ushort authority, ushort additional)
    {
        U16(id);
        U16(flags);
        U16(questions);
        U16(answers);
        U16(authority);
        U16(additional);
    }

    public void Question(string name, ushort type, ushort cls)
    {
        Name(name);
        U16(type);
        U16(cls);
    }

    public void Ptr(string name, uint ttl, string target) =>
        Record(name, DnsType.Ptr, 1, ttl, w => w.Name(target));

    public void Srv(string name, uint ttl, ushort port, string target, bool cacheFlush = true) =>
        Record(name, DnsType.Srv, (ushort)(cacheFlush ? 0x8001 : 1), ttl, w =>
        {
            w.U16(0);
            w.U16(0);
            w.U16(port);
            w.Name(target);
        });

    public void Txt(string name, uint ttl, IEnumerable<KeyValuePair<string, string>> values, bool cacheFlush = true) =>
        Record(name, DnsType.Txt, (ushort)(cacheFlush ? 0x8001 : 1), ttl, w =>
        {
            var any = false;
            foreach (var (key, value) in values)
            {
                var bytes = Encoding.UTF8.GetBytes($"{key}={value}");
                if (bytes.Length > 255)
                {
                    bytes = bytes[..255];
                }

                w._buffer.Add((byte)bytes.Length);
                w._buffer.AddRange(bytes);
                any = true;
            }

            if (!any)
            {
                w._buffer.Add(0);
            }
        });

    public void A(string name, uint ttl, IPAddress address, bool cacheFlush = true) =>
        Record(name, DnsType.A, (ushort)(cacheFlush ? 0x8001 : 1), ttl, w => w._buffer.AddRange(address.GetAddressBytes()));

    public void Record(string name, ushort type, ushort cls, uint ttl, Action<DnsWriter> writeData)
    {
        Name(name);
        U16(type);
        U16(cls);
        U32(ttl);
        var lengthPosition = _buffer.Count;
        U16(0);
        var start = _buffer.Count;
        writeData(this);
        var length = _buffer.Count - start;
        _buffer[lengthPosition] = (byte)(length >> 8);
        _buffer[lengthPosition + 1] = (byte)(length & 0xFF);
    }

    public void Name(string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length > 63)
            {
                bytes = bytes[..63];
            }

            _buffer.Add((byte)bytes.Length);
            _buffer.AddRange(bytes);
        }

        _buffer.Add(0);
    }

    public void U16(ushort value)
    {
        _buffer.Add((byte)(value >> 8));
        _buffer.Add((byte)(value & 0xFF));
    }

    public void U32(uint value)
    {
        _buffer.Add((byte)(value >> 24));
        _buffer.Add((byte)((value >> 16) & 0xFF));
        _buffer.Add((byte)((value >> 8) & 0xFF));
        _buffer.Add((byte)(value & 0xFF));
    }

    public byte[] ToArray() => _buffer.ToArray();
}
