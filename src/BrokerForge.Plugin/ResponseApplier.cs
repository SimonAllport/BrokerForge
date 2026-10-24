using System;
using BrokerForge.Contracts;
using Microsoft.Xrm.Sdk;

namespace BrokerForge.Plugin;

/// <summary>Writes the host's answer into the plug-in's output parameters.</summary>
public static class ResponseApplier
{
    public static void Apply(IPluginExecutionContext context, CanonicalRequest request, CanonicalResponse response)
    {
        switch (request.Message)
        {
            case BrokerMessage.Retrieve:
                var record = response.Record ?? throw new MappingException("record", "Host returned no record for Retrieve.");
                context.OutputParameters["BusinessEntity"] = EntityMapper.ToEntity(request.Table!, record);
                break;

            case BrokerMessage.RetrieveMultiple:
                var rows = response.Rows ?? throw new MappingException("rows", "Host returned no rows collection for RetrieveMultiple.");
                var collection = new EntityCollection { EntityName = request.Table, MoreRecords = response.Next != null, PagingCookie = response.Next };
                foreach (var row in rows)
                {
                    collection.Entities.Add(EntityMapper.ToEntity(request.Table!, row));
                }

                context.OutputParameters["BusinessEntityCollection"] = collection;
                break;

            case BrokerMessage.Create:
                Guid id;
                if (response.Id == null || !Guid.TryParse(response.Id, out id))
                {
                    throw new MappingException("id", "Host returned no valid id for Create.");
                }

                context.OutputParameters["id"] = id;
                break;

            case BrokerMessage.CustomApi:
                foreach (var output in response.Outputs ?? new System.Collections.Generic.List<FieldValue>())
                {
                    context.OutputParameters[output.Name] = EntityMapper.FromField(output);
                }

                break;

            case BrokerMessage.Update:
            case BrokerMessage.Delete:
                break;
        }
    }
}

/// <summary>
/// Turns a typed failure into the error Dataverse shows. Every message carries the correlation ID.
/// Phase 1: consider Microsoft.CrmSdk.Data exceptions (ObjectNotFoundException, InvalidQueryException) for richer platform handling.
/// </summary>
public static class ErrorTranslator
{
    public static InvalidPluginExecutionException ToPluginException(BrokerForgeException ex)
    {
        var error = ex.ToError();
        var message = error.ToString();
        if (ex is ConnectionRequiredException connect)
        {
            message += " | Connect: " + connect.ConnectUrl;
        }

        return new InvalidPluginExecutionException(OperationStatus.Failed, message);
    }
}
