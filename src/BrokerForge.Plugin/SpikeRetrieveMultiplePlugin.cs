using System;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace BrokerForge.Plugin
{
    /// <summary>
    /// Phase 0 spike. Registered as the RetrieveMultiple plug-in of a virtual table data provider.
    /// Exercises the three surfaces phase 0 has to verify, in one call:
    ///   1. reads the data source record through IEntityDataSourceRetrieverService,
    ///   2. acquires a Power Platform managed identity token through IManagedIdentityService,
    ///   3. calls the stub host with it and returns a hard-coded row carrying the host's reply.
    /// Every failure throws; nothing is skipped or defaulted.
    /// </summary>
    public sealed class SpikeRetrieveMultiplePlugin : IPlugin
    {
        // Columns on the bf_spikedatasource data source table. No secrets: URL and scope only.
        private const string HostUrlColumn = "bf_hosturl";
        private const string ScopeColumn = "bf_scope";

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var correlationId = context.CorrelationId;
            tracing.Trace("correlationId={0} message={1} entity={2}", correlationId, context.MessageName, context.PrimaryEntityName);

            var retriever = (IEntityDataSourceRetrieverService)serviceProvider.GetService(typeof(IEntityDataSourceRetrieverService));
            if (retriever == null)
                throw new InvalidPluginExecutionException("IEntityDataSourceRetrieverService is not available in this context.");

            var dataSource = retriever.RetrieveEntityDataSource();
            if (dataSource == null)
                throw new InvalidPluginExecutionException("RetrieveEntityDataSource returned no data source record.");
            tracing.Trace("dataSource logicalName={0} id={1} attributes={2}",
                dataSource.LogicalName, dataSource.Id, string.Join(",", dataSource.Attributes.Keys));

            var hostUrl = RequireString(dataSource, HostUrlColumn);
            var scope = RequireString(dataSource, ScopeColumn);

            var identity = (IManagedIdentityService)serviceProvider.GetService(typeof(IManagedIdentityService));
            if (identity == null)
                throw new InvalidPluginExecutionException("IManagedIdentityService is not available in this context.");

            var token = identity.AcquireToken(new[] { scope });
            tracing.Trace("acquired managed identity token for scope {0} (length {1})", scope, token?.Length ?? 0);

            var hostReply = CallHost(hostUrl, token, correlationId);
            tracing.Trace("host replied: {0}", hostReply);

            // Paging probe (phase 1, Task 1): report what the platform passes in, and page through
            // three pages of PageInfo.Count rows so the cookie round-trip can be observed.
            var query = context.InputParameters.Contains("Query") ? context.InputParameters["Query"] as QueryExpression : null;
            var page = query?.PageInfo;
            var pageNumber = page?.PageNumber ?? 0;
            var count = page != null && page.Count > 0 ? page.Count : 3;
            var probe = string.Format("queryType={0} pageNumber={1} count={2} cookie={3} returnTotal={4} topCount={5}",
                context.InputParameters.Contains("Query") ? context.InputParameters["Query"]?.GetType().Name : "none",
                pageNumber, page?.Count, page?.PagingCookie ?? "(null)", page?.ReturnTotalRecordCount, query?.TopCount);
            tracing.Trace("paging probe: {0}", probe);

            var effectivePage = pageNumber < 1 ? 1 : pageNumber;
            var collection = new EntityCollection { EntityName = context.PrimaryEntityName };
            var rowsThisPage = effectivePage == 1 && count > 1 ? count - 1 : count; // short first page
            for (var i = 0; i < rowsThisPage; i++)
            {
                var n = effectivePage * 1000 + i;
                var row = new Entity(context.PrimaryEntityName, new Guid(n, 0, 0, new byte[8]));
                row["bf_name"] = "page " + effectivePage + " row " + i;
                row["bf_hostreply"] = probe;
                collection.Entities.Add(row);
            }
            collection.MoreRecords = effectivePage < 3;
            if (collection.MoreRecords)
                collection.PagingCookie = "bf-test-" + effectivePage;
            context.OutputParameters["BusinessEntityCollection"] = collection;
        }

        private static string CallHost(string hostUrl, string token, Guid correlationId)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, hostUrl.TrimEnd('/') + "/spike/whoami"))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Add("x-correlation-id", correlationId.ToString());

                HttpResponseMessage response;
                try
                {
                    response = Http.SendAsync(request).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    throw new InvalidPluginExecutionException(
                        string.Format("Broker host unreachable at {0} (correlationId {1}): {2}", hostUrl, correlationId, ex.Message), ex);
                }

                var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                    throw new InvalidPluginExecutionException(
                        string.Format("Broker host returned {0} (correlationId {1}): {2}", (int)response.StatusCode, correlationId, body));
                return body;
            }
        }

        private static string RequireString(Entity entity, string column)
        {
            var value = entity.GetAttributeValue<string>(column);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidPluginExecutionException(
                    string.Format("Data source record {0} has no value for {1}.", entity.Id, column));
            return value;
        }
    }
}
