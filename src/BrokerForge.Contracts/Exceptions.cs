using System;

namespace BrokerForge.Contracts;

/// <summary>
/// Base for every typed BrokerForge failure (HLD 11). Messages name the cause and never contain secrets.
/// Each exception converts to and from <see cref="BrokerError"/> so it survives the hop between host and plug-in.
/// </summary>
public abstract class BrokerForgeException : Exception
{
    protected BrokerForgeException(ErrorCode code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    public ErrorCode Code { get; }

    public string? CorrelationId { get; set; }

    public virtual BrokerError ToError() => new BrokerError
    {
        Code = Code,
        Message = Message,
        CorrelationId = CorrelationId,
    };

    /// <summary>Rebuilds the typed exception from an error envelope.</summary>
    public static BrokerForgeException FromError(BrokerError error)
    {
        BrokerForgeException ex = error.Code switch
        {
            ErrorCode.UnsupportedQuery => new UnsupportedQueryException(error.Column ?? "", error.Operator ?? "", error.Message),
            ErrorCode.ConnectionRequired => new ConnectionRequiredException(error.Message, error.ConnectUrl ?? ""),
            ErrorCode.Source => new SourceException(error.StatusCode, error.Message),
            ErrorCode.SourceTimeout => new SourceTimeoutException(error.Message),
            ErrorCode.Mapping => new MappingException(error.Path ?? "", error.Message),
            ErrorCode.BrokerUnavailable => new BrokerUnavailableException(error.Message),
            ErrorCode.Configuration => new BrokerConfigurationException(error.Message),
            _ => new SourceMappedException(error.Code, error.Message),
        };
        ex.CorrelationId = error.CorrelationId;
        return ex;
    }
}

/// <summary>A filter, operator, sort or paging feature the descriptor does not declare. Never filtered client-side.</summary>
public sealed class UnsupportedQueryException : BrokerForgeException
{
    public UnsupportedQueryException(string column, string op, string reason)
        : base(ErrorCode.UnsupportedQuery, $"Unsupported query: {reason} (column '{column}', operator '{op}').")
    {
        Column = column;
        Operator = op;
    }

    public string Column { get; }
    public string Operator { get; }

    public override BrokerError ToError()
    {
        var e = base.ToError();
        e.Column = Column;
        e.Operator = Operator;
        return e;
    }
}

/// <summary>A per-user mode has no credential or consent for this user. Never falls back to the service account.</summary>
public sealed class ConnectionRequiredException : BrokerForgeException
{
    public ConnectionRequiredException(string message, string connectUrl)
        : base(ErrorCode.ConnectionRequired, message)
    {
        ConnectUrl = connectUrl;
    }

    public string ConnectUrl { get; }

    public override BrokerError ToError()
    {
        var e = base.ToError();
        e.ConnectUrl = ConnectUrl;
        return e;
    }
}

/// <summary>The source returned a status the descriptor does not map.</summary>
public sealed class SourceException : BrokerForgeException
{
    public SourceException(int? statusCode, string message, Exception? inner = null)
        : base(ErrorCode.Source, message, inner)
    {
        StatusCode = statusCode;
    }

    public int? StatusCode { get; }

    public override BrokerError ToError()
    {
        var e = base.ToError();
        e.StatusCode = StatusCode;
        return e;
    }
}

/// <summary>The source did not answer within the host limit (90 seconds by default).</summary>
public sealed class SourceTimeoutException : BrokerForgeException
{
    public SourceTimeoutException(string message, Exception? inner = null)
        : base(ErrorCode.SourceTimeout, message, inner) { }
}

/// <summary>The response did not match the descriptor's map, or a value could not be coerced.</summary>
public sealed class MappingException : BrokerForgeException
{
    public MappingException(string path, string message, Exception? inner = null)
        : base(ErrorCode.Mapping, message, inner)
    {
        Path = path;
    }

    public string Path { get; }

    public override BrokerError ToError()
    {
        var e = base.ToError();
        e.Path = Path;
        return e;
    }
}

/// <summary>The plug-in could not reach the broker host, or the host failed outside the domain envelope.</summary>
public sealed class BrokerUnavailableException : BrokerForgeException
{
    public BrokerUnavailableException(string message, Exception? inner = null)
        : base(ErrorCode.BrokerUnavailable, message, inner) { }
}

/// <summary>Unknown service instance, operation or object map, invalid descriptor, or an auth mode not yet available.</summary>
public sealed class BrokerConfigurationException : BrokerForgeException
{
    public BrokerConfigurationException(string message, Exception? inner = null)
        : base(ErrorCode.Configuration, message, inner) { }
}

/// <summary>
/// A source status the descriptor maps to a Dataverse error: NotFound, Conflict, Forbidden or Validation (descriptor spec 8).
/// </summary>
public sealed class SourceMappedException : BrokerForgeException
{
    public SourceMappedException(ErrorCode code, string message)
        : base(code, message)
    {
        if (code != ErrorCode.NotFound && code != ErrorCode.Conflict && code != ErrorCode.Forbidden && code != ErrorCode.Validation)
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "Only NotFound, Conflict, Forbidden and Validation are source-mapped errors.");
        }
    }
}
