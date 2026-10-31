using System.Globalization;
using System.Text.RegularExpressions;
using BrokerForge.Abstractions;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Mapping;
using BrokerForge.Descriptors.Model;
using BrokerForge.Descriptors.Planning;
using BrokerForge.Descriptors.Rendering;

namespace BrokerForge.Host.Execution;

/// <summary>
/// Reads one Dataverse page (descriptor spec 7). It fills the requested page with up to
/// paging.maxCallsPerPage source calls; when the budget runs out first it returns the rows collected
/// with a next token (D-12). Every call shares the request deadline. Pages are never padded, trimmed
/// or filtered client-side. <paramref name="tokenKey"/> signs the page tokens it hands out.
/// </summary>
public sealed partial class PagingCoordinator(OperationExecutor executor, ISecretResolver secrets, byte[] tokenKey)
{
    [GeneratedRegex(@"<([^>]+)>\s*;\s*rel=""?next""?", RegexOptions.IgnoreCase)]
    private static partial Regex NextLink();

    public async Task<(IReadOnlyList<MappedRow> Rows, string? Next)> ReadPageAsync(OperationContext op, QueryPlan plan, Deadline deadline, CancellationToken ct)
    {
        var d = op.Descriptor;
        var paging = d.Paging ?? throw new InvalidOperationException($"{d.Source}: RetrieveMultiple has no paging block");
        var correlationId = op.Binding.CorrelationId;
        var strategy = paging.Strategy;
        var target = plan.RequestedSize;
        UnsupportedQueryException Reject(string why) => new($"{why} (operation {d.Operation}).", correlationId);

        // Strategy state. The token, when present, has already been checked against the operation, version and query.
        string? cursor = null, link = null;
        var offset = 0;
        var pageNumber = paging.Page?.Base ?? 1;
        var fixedSize = Math.Min(plan.PageSize, target);
        if (plan.Token != null)
        {
            var (tokenStrategy, c) = PageToken.Parse(plan.Token, d, plan, tokenKey, correlationId);
            if (strategy == "none")
                throw Reject("This source returns everything in one page; there is no next page to request");
            if (tokenStrategy != strategy)
                throw Reject($"The page token is for {tokenStrategy} paging, but the operation uses {strategy}");
            switch (strategy)
            {
                case "cursor": cursor = c; break;
                case "offset":
                    offset = int.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out var o) ? o : throw Reject("The page token's offset is corrupt");
                    break;
                case "pageNumber":
                    var parts = c.Split(':');
                    if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out pageNumber)
                        || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out fixedSize) || fixedSize < 1)
                        throw Reject("The page token's page position is corrupt");
                    break;
                case "link":
                    var space = c.IndexOf(' ');
                    if (space < 1 || !int.TryParse(c[..space], NumberStyles.None, CultureInfo.InvariantCulture, out fixedSize) || fixedSize < 1)
                        throw Reject("The page token's link is corrupt");
                    link = c[(space + 1)..];
                    if (!UnderBase(link, op.Instance.BaseUrl))
                        throw Reject("The page token's link is outside the service instance base URL");
                    break;
            }
        }

        var rows = new List<MappedRow>();
        var budget = strategy == "none" ? 1 : paging.MaxCallsPerPage;
        var calls = 0;
        bool more;
        while (true)
        {
            var remaining = target - rows.Count;
            var fixedStep = strategy is "pageNumber" or "link";
            var callSize = fixedStep ? fixedSize : Math.Min(plan.PageSize, remaining);

            var binding = op.Binding with { Plan = plan, Paging = new PagingValues(callSize, cursor, offset, pageNumber) };
            var request = await RequestRenderer.RenderAsync(d.Request, d, binding, secrets, ct);
            if (link != null)
            {
                // A link may change the query (its cursor) but never the operation's path.
                var uri = new Uri(link);
                if (uri.AbsolutePath != RenderedPath(request))
                    throw plan.Token != null && calls == 0
                        ? Reject("The page token's link is not for this operation's path")
                        : new SourceException($"The source's next link is not for this operation's path (operation {d.Operation}).", correlationId);
                request = request with { BaseUrl = uri.GetLeftPart(UriPartial.Authority), PathAndQuery = uri.PathAndQuery };
            }

            var result = await executor.ExecuteAsync(d, request, op.Channel, deadline, correlationId, ct);
            ErrorMapper.ThrowIfError(d, result, correlationId, SecretValues(request));
            var page = ResponseMapper.MapRows(d, result, op.Keys.Kind, correlationId);
            rows.AddRange(page);
            calls++;

            SourceException Source(string why) => new($"{why} (operation {d.Operation}).", correlationId, result.Status);
            switch (strategy)
            {
                case "cursor":
                    var next = ResponseMapper.SelectScalar(result.Body, paging.Token!.Next, m => Source(m));
                    more = !string.IsNullOrEmpty(next);
                    if (more && next == cursor)
                        throw Source("The source returned the same cursor again, so paging would never end");
                    cursor = next;
                    break;
                case "offset":
                    offset += page.Count;
                    if (paging.Offset!.Total is { } totalPath)
                    {
                        var total = ResponseMapper.SelectScalar(result.Body, totalPath, m => Source(m));
                        more = int.TryParse(total, NumberStyles.None, CultureInfo.InvariantCulture, out var t)
                            ? offset < t
                            : throw Source($"The total at {totalPath} is not a whole number");
                    }
                    else
                    {
                        more = page.Count == callSize;
                    }
                    more &= page.Count > 0;
                    break;
                case "pageNumber":
                    more = page.Count == callSize;
                    pageNumber++;
                    break;
                case "link":
                    link = NextFrom(result, request, op.Instance.BaseUrl, Source);
                    more = link != null;
                    break;
                default:
                    more = false;
                    break;
            }

            if (!more || rows.Count >= target || calls >= budget)
                break;
            // A fixed-size call that would overflow the page waits for the next Dataverse page instead.
            if (fixedStep && fixedSize > target - rows.Count)
                break;
        }

        if (!more)
            return (rows, null);
        var state = strategy switch
        {
            "cursor" => cursor!,
            "offset" => offset.ToString(CultureInfo.InvariantCulture),
            "pageNumber" => $"{pageNumber}:{fixedSize}",
            "link" => $"{fixedSize} {link}",
            _ => throw new InvalidOperationException(strategy),
        };
        return (rows, PageToken.Create(d, strategy, state, plan, tokenKey));
    }

    private static string? NextFrom(BrokerResult result, RenderedRequest request, string baseUrl, Func<string, SourceException> fail)
    {
        if (!result.Headers.TryGetValue("Link", out var values))
            return null;
        foreach (var value in values)
            if (NextLink().Match(value) is { Success: true } m)
            {
                var url = new Uri(new Uri(request.BaseUrl.TrimEnd('/') + request.PathAndQuery), m.Groups[1].Value).AbsoluteUri;
                return UnderBase(url, baseUrl) ? url : throw fail("The source's next link points outside the service instance base URL");
            }
        return null;
    }

    private static string RenderedPath(RenderedRequest request) =>
        new Uri(request.BaseUrl.TrimEnd('/') + request.PathAndQuery).AbsolutePath;

    /// <summary>True when <paramref name="url"/> has the base URL's scheme, host and port, no user info, and a path under the base path.</summary>
    public static bool UnderBase(string url, string baseUrl)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var b))
            return false;
        var basePath = b.AbsolutePath.TrimEnd('/') + "/";
        return u.Scheme == b.Scheme
            && string.Equals(u.Host, b.Host, StringComparison.OrdinalIgnoreCase)
            && u.Port == b.Port
            && string.IsNullOrEmpty(u.UserInfo)
            && (u.AbsolutePath + "/").StartsWith(basePath, StringComparison.Ordinal);
    }

    public static IEnumerable<string> SecretValues(RenderedRequest request)
    {
        if (request.SecretQueryParams.Count == 0 || request.PathAndQuery.IndexOf('?') is not (var q and >= 0))
            yield break;
        foreach (var pair in request.PathAndQuery[(q + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && request.SecretQueryParams.Contains(Uri.UnescapeDataString(pair[..eq])))
                yield return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
    }
}
