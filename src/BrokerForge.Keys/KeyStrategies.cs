using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using BrokerForge.Contracts;

namespace BrokerForge.Keys;

public enum KeyKind
{
    Int32,
    Int64,
    String,
    Guid,
    Composite,
}

/// <summary>
/// A source record's key. <see cref="Value"/> is the invariant string form;
/// <see cref="Parts"/> is set only for composite keys.
/// </summary>
public sealed record ExternalKey(KeyKind Kind, string Value, IReadOnlyDictionary<string, string>? Parts = null)
{
    public static ExternalKey FromInt32(int value) => new(KeyKind.Int32, value.ToString(CultureInfo.InvariantCulture));

    public static ExternalKey FromInt64(long value) => new(KeyKind.Int64, value.ToString(CultureInfo.InvariantCulture));

    public static ExternalKey FromString(string value) => new(KeyKind.String, value);

    public static ExternalKey FromGuid(Guid value) => new(KeyKind.Guid, value.ToString("D"));
}

/// <summary>Maps between Dataverse GUIDs and source keys for one object map (HLD 8).</summary>
public interface IKeyStrategy
{
    KeyStrategy Strategy { get; }

    /// <summary>Throws <see cref="MappingException"/> when the key cannot be represented by this strategy.</summary>
    Guid Encode(ExternalKey key);

    /// <summary>False when the GUID was not produced by this strategy; callers treat that as not found.</summary>
    bool TryDecode(Guid id, out ExternalKey key);
}

/// <summary>Source key is already a GUID: used as is.</summary>
public sealed class NativeGuidKeyStrategy : IKeyStrategy
{
    public KeyStrategy Strategy => KeyStrategy.NativeGuid;

    public Guid Encode(ExternalKey key)
    {
        if (key.Kind != KeyKind.Guid || !Guid.TryParse(key.Value, out var id))
        {
            throw new MappingException("key", $"Native GUID strategy needs a GUID key; got {key.Kind}.");
        }

        return id;
    }

    public bool TryDecode(Guid id, out ExternalKey key)
    {
        key = ExternalKey.FromGuid(id);
        return id != Guid.Empty;
    }
}

/// <summary>
/// Packs keys up to 15 bytes into the GUID itself, so decoding needs no lookup (HLD 8, decision D-04).
/// Layout: byte 0 is a marker for the key kind; Int32 uses bytes 1–4 and Int64 bytes 1–8, big-endian;
/// String uses byte 1 for the UTF-8 length (0–14) and bytes 2–15 for the text. Unused bytes must be zero.
/// </summary>
public sealed class ReversibleKeyStrategy : IKeyStrategy
{
    public const int MaxStringBytes = 14;

    private const byte Int32Marker = 0xB1;
    private const byte Int64Marker = 0xB2;
    private const byte StringMarker = 0xB3;

    public KeyStrategy Strategy => KeyStrategy.Reversible;

    public Guid Encode(ExternalKey key)
    {
        var bytes = new byte[16];
        switch (key.Kind)
        {
            case KeyKind.Int32:
                bytes[0] = Int32Marker;
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(1, 4), int.Parse(key.Value, NumberStyles.Integer, CultureInfo.InvariantCulture));
                break;
            case KeyKind.Int64:
                bytes[0] = Int64Marker;
                BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(1, 8), long.Parse(key.Value, NumberStyles.Integer, CultureInfo.InvariantCulture));
                break;
            case KeyKind.String:
                var text = Encoding.UTF8.GetBytes(key.Value);
                if (text.Length > MaxStringBytes)
                {
                    throw new MappingException(
                        "key",
                        $"Key is {text.Length} UTF-8 bytes; reversible encoding holds at most {MaxStringBytes}. Use the key-map strategy for this object map.");
                }

                bytes[0] = StringMarker;
                bytes[1] = (byte)text.Length;
                text.CopyTo(bytes, 2);
                break;
            default:
                throw new MappingException("key", $"Reversible encoding does not support {key.Kind} keys. Use native GUID or key map.");
        }

        return new Guid(bytes);
    }

    public bool TryDecode(Guid id, out ExternalKey key)
    {
        key = default!;
        var bytes = id.ToByteArray();
        switch (bytes[0])
        {
            case Int32Marker when AllZero(bytes, 5):
                key = ExternalKey.FromInt32(BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(1, 4)));
                return true;
            case Int64Marker when AllZero(bytes, 9):
                key = ExternalKey.FromInt64(BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(1, 8)));
                return true;
            case StringMarker when bytes[1] <= MaxStringBytes && AllZero(bytes, 2 + bytes[1]):
                try
                {
                    var decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                    key = ExternalKey.FromString(decoder.GetString(bytes, 2, bytes[1]));
                    return true;
                }
                catch (DecoderFallbackException)
                {
                    return false;
                }
            default:
                return false;
        }
    }

    private static bool AllZero(byte[] bytes, int from)
    {
        for (var i = from; i < bytes.Length; i++)
        {
            if (bytes[i] != 0)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Composite or long keys via <c>bf_keymap</c> rows. Phase 5. Pruning must skip GUIDs referenced from native tables (HLD 16 open question).
/// </summary>
public sealed class KeyMapKeyStrategy : IKeyStrategy
{
    public KeyStrategy Strategy => KeyStrategy.KeyMap;

    public Guid Encode(ExternalKey key) =>
        throw new NotImplementedException("Phase 5: key-map strategy (bf_keymap upsert on first sight of a key).");

    public bool TryDecode(Guid id, out ExternalKey key) =>
        throw new NotImplementedException("Phase 5: key-map strategy (bf_keymap lookup).");
}

public static class KeyStrategyFactory
{
    private static readonly IKeyStrategy Native = new NativeGuidKeyStrategy();
    private static readonly IKeyStrategy Reversible = new ReversibleKeyStrategy();
    private static readonly IKeyStrategy KeyMap = new KeyMapKeyStrategy();

    public static IKeyStrategy For(KeyStrategy strategy) => strategy switch
    {
        KeyStrategy.NativeGuid => Native,
        KeyStrategy.Reversible => Reversible,
        KeyStrategy.KeyMap => KeyMap,
        _ => throw new BrokerConfigurationException($"Unknown key strategy {strategy}."),
    };
}
