namespace BrokerForge.Contracts;

/// <summary>The Dataverse message (or Custom API) a canonical request represents.</summary>
public enum BrokerMessage
{
    Retrieve,
    RetrieveMultiple,
    Create,
    Update,
    Delete,
    CustomApi,
}

/// <summary>K2-equivalent service instance authentication modes (HLD 9.1).</summary>
public enum AuthMode
{
    ServiceAccount,
    Static,
    Impersonate,
    Sso,
    OAuth,
}

/// <summary>How a virtual table GUID maps to the source's key (HLD 8).</summary>
public enum KeyStrategy
{
    NativeGuid,
    Reversible,
    KeyMap,
}

/// <summary>
/// Type of a value carried in the canonical contract. Values always travel as invariant strings;
/// the type says how to parse them (see <see cref="FieldValue"/>).
/// </summary>
public enum FieldType
{
    String,
    Int,
    Long,
    Decimal,
    Double,
    Bool,
    DateTime,
    Date,
    Guid,
    OptionSet,
    Money,
}

/// <summary>Error codes carried in <see cref="BrokerError"/>; one per row of HLD 11 plus configuration.</summary>
public enum ErrorCode
{
    UnsupportedQuery,
    ConnectionRequired,
    Source,
    SourceTimeout,
    Mapping,
    BrokerUnavailable,
    Configuration,
    NotFound,
    Conflict,
    Forbidden,
    Validation,
}

/// <summary>Filter operators the canonical contract can express. Names are the wire values.</summary>
public static class FilterOperators
{
    public const string Eq = "eq";
    public const string Ne = "ne";
    public const string Gt = "gt";
    public const string Ge = "ge";
    public const string Lt = "lt";
    public const string Le = "le";
    public const string In = "in";
    public const string Like = "like";
    public const string Null = "null";
    public const string NotNull = "notnull";

    public static readonly string[] All = { Eq, Ne, Gt, Ge, Lt, Le, In, Like, Null, NotNull };
}

/// <summary>Version of the canonical contract carried on every request and response.</summary>
public static class ContractVersions
{
    public const string Current = "1";
}
