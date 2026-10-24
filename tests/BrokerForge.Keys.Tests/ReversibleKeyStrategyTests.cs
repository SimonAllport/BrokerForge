using System.Text;
using BrokerForge.Contracts;
using BrokerForge.Keys;
using FsCheck;
using FsCheck.Xunit;

namespace BrokerForge.Keys.Tests;

public class ReversibleKeyStrategyTests
{
    private readonly ReversibleKeyStrategy _keys = new();

    [Property(MaxTest = 1000)]
    public bool Int32_round_trips(int value) => RoundTrips(ExternalKey.FromInt32(value));

    [Property(MaxTest = 1000)]
    public bool Int64_round_trips(long value) => RoundTrips(ExternalKey.FromInt64(value));

    [Property(MaxTest = 1000)]
    public Property Strings_up_to_14_utf8_bytes_round_trip(NonNull<string> value)
    {
        var fits = Encoding.UTF8.GetByteCount(value.Get) <= ReversibleKeyStrategy.MaxStringBytes;
        return Prop.When(fits && IsValidUtf16(value.Get), () => RoundTrips(ExternalKey.FromString(value.Get)));
    }

    [Property(MaxTest = 1000)]
    public bool Distinct_keys_never_share_a_guid(int a, int b) =>
        a == b || _keys.Encode(ExternalKey.FromInt32(a)) != _keys.Encode(ExternalKey.FromInt32(b));

    [Property(MaxTest = 1000)]
    public bool Int32_and_string_of_same_text_do_not_collide(int value) =>
        _keys.Encode(ExternalKey.FromInt32(value)) != _keys.Encode(ExternalKey.FromString(value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    [Property(MaxTest = 1000)]
    public bool Random_guids_are_not_decoded_as_keys(Guid id)
    {
        // A GUID this strategy did not produce must decode to "not found", never to a wrong key.
        if (!_keys.TryDecode(id, out var key))
        {
            return true;
        }

        return _keys.Encode(key) == id;
    }

    [Fact]
    public void String_over_14_bytes_fails_loudly_and_names_the_alternative()
    {
        var ex = Assert.Throws<MappingException>(() => _keys.Encode(ExternalKey.FromString("ORD-000000000001")));
        Assert.Contains("key-map", ex.Message);
    }

    [Fact]
    public void Composite_keys_are_rejected()
    {
        var composite = new ExternalKey(KeyKind.Composite, "{}", new Dictionary<string, string> { ["a"] = "1" });
        Assert.Throws<MappingException>(() => _keys.Encode(composite));
    }

    [Fact]
    public void Empty_guid_is_not_a_key()
    {
        Assert.False(_keys.TryDecode(Guid.Empty, out _));
    }

    [Fact]
    public void Native_guid_strategy_round_trips_and_rejects_non_guid_keys()
    {
        var native = new NativeGuidKeyStrategy();
        var id = Guid.NewGuid();
        Assert.True(native.TryDecode(native.Encode(ExternalKey.FromGuid(id)), out var key));
        Assert.Equal(id.ToString("D"), key.Value);
        Assert.Throws<MappingException>(() => native.Encode(ExternalKey.FromInt32(1)));
    }

    private bool RoundTrips(ExternalKey original) =>
        _keys.TryDecode(_keys.Encode(original), out var decoded) && decoded == original;

    private static bool IsValidUtf16(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(s[i]))
            {
                return false;
            }
        }

        return true;
    }
}
