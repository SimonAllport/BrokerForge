using System;
using System.Collections.Generic;
using BrokerForge.Contracts;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace BrokerForge.Plugin;

/// <summary>Builds the canonical request from the plug-in context. Carries intent only; the host does the rest.</summary>
public static class RequestBuilder
{
    public static CanonicalRequest ForDataProvider(IPluginExecutionContext context, DataSourceSettings settings)
    {
        var table = context.PrimaryEntityName;
        var request = NewRequest(context, settings.ServiceInstance, settings.EnvironmentId);
        request.Table = table;

        switch (context.MessageName)
        {
            case "Retrieve":
                request.Message = BrokerMessage.Retrieve;
                request.Id = TargetReference(context).Id.ToString("D");
                break;

            case "RetrieveMultiple":
                request.Message = BrokerMessage.RetrieveMultiple;
                var query = context.InputParameters["Query"] as QueryExpression
                    ?? throw new UnsupportedQueryException("", "query", "only QueryExpression queries reach the provider; got " + DescribeType(context.InputParameters["Query"]));
                request.Query = QueryTranslator.Translate(query);
                break;

            case "Create":
                request.Message = BrokerMessage.Create;
                var created = TargetEntity(context);
                if (created.Id != Guid.Empty)
                {
                    throw new MappingException("id", "Create with a caller-supplied id is not supported; the source assigns keys.");
                }

                request.Record = EntityMapper.ToFields(created, table + "id");
                break;

            case "Update":
                request.Message = BrokerMessage.Update;
                var updated = TargetEntity(context);
                request.Id = updated.Id.ToString("D");
                request.Record = EntityMapper.ToFields(updated, table + "id");
                break;

            case "Delete":
                request.Message = BrokerMessage.Delete;
                request.Id = TargetReference(context).Id.ToString("D");
                break;

            default:
                throw new BrokerConfigurationException("The BrokerForge data provider does not handle message '" + context.MessageName + "'.");
        }

        return request;
    }

    public static CanonicalRequest ForCustomApi(IPluginExecutionContext context, CustomApiSettings settings)
    {
        var request = NewRequest(context, settings.ServiceInstance, settings.EnvironmentId);
        request.Message = BrokerMessage.CustomApi;
        request.CustomApi = context.MessageName;
        request.Inputs = new List<FieldValue>();
        foreach (var parameter in context.InputParameters)
        {
            request.Inputs.Add(EntityMapper.ToField(parameter.Key, parameter.Value));
        }

        return request;
    }

    private static CanonicalRequest NewRequest(IPluginExecutionContext context, string serviceInstance, string environmentId)
    {
        var request = new CanonicalRequest
        {
            CorrelationId = context.CorrelationId.ToString("D"),
            EnvironmentId = environmentId,
            ServiceInstance = serviceInstance,
        };

        request.Caller.Async = context.Mode == 1;
        if (context is IPluginExecutionContext2 context2 && context2.InitiatingUserAzureActiveDirectoryObjectId != Guid.Empty)
        {
            request.Caller.UserObjectId = context2.InitiatingUserAzureActiveDirectoryObjectId.ToString("D");
        }

        return request;
    }

    private static EntityReference TargetReference(IPluginExecutionContext context) =>
        context.InputParameters["Target"] as EntityReference
        ?? throw new BrokerConfigurationException(context.MessageName + " expected an EntityReference Target.");

    private static Entity TargetEntity(IPluginExecutionContext context) =>
        context.InputParameters["Target"] as Entity
        ?? throw new BrokerConfigurationException(context.MessageName + " expected an Entity Target.");

    private static string DescribeType(object? value) => value == null ? "nothing" : value.GetType().Name;
}
