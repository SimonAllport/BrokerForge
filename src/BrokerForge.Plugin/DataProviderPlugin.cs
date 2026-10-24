using System;
using BrokerForge.Contracts;
using Microsoft.Xrm.Sdk;

namespace BrokerForge.Plugin;

/// <summary>
/// The generic virtual table data provider. Register this one type for Retrieve, RetrieveMultiple, Create, Update
/// and Delete on the BrokerForge entitydataprovider row; every virtual table bound to a BrokerForge data source uses it.
/// </summary>
public sealed class DataProviderPlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
        var trace = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

        var correlationId = context.CorrelationId.ToString("D");
        trace.Trace("BrokerForge {0} on {1}, correlationId {2}", context.MessageName, context.PrimaryEntityName, correlationId);

        try
        {
            var settings = DataSourceSettings.Resolve(serviceProvider);
            var request = RequestBuilder.ForDataProvider(context, settings);
            var client = new BrokerHostClient(settings.HostUrl, new ManagedIdentityTokenSource(serviceProvider, settings.HostScope));
            var response = client.Execute(request);
            ResponseApplier.Apply(context, request, response);
        }
        catch (BrokerForgeException ex)
        {
            if (ex.CorrelationId == null)
            {
                ex.CorrelationId = correlationId;
            }

            trace.Trace("BrokerForge failure: {0}", ex.ToError());
            throw ErrorTranslator.ToPluginException(ex);
        }
    }
}

/// <summary>
/// Routes every generated Custom API to the host. Register as the plug-in type of each Custom API the generator creates.
/// </summary>
public sealed class CustomApiPlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
        var trace = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
        trace.Trace("BrokerForge Custom API {0}, correlationId {1}", context.MessageName, context.CorrelationId);

        try
        {
            var settings = CustomApiSettings.Resolve(serviceProvider, context);
            var request = RequestBuilder.ForCustomApi(context, settings);
            var client = new BrokerHostClient(settings.HostUrl, new ManagedIdentityTokenSource(serviceProvider, settings.HostScope));
            var response = client.Execute(request);
            ResponseApplier.Apply(context, request, response);
        }
        catch (BrokerForgeException ex)
        {
            if (ex.CorrelationId == null)
            {
                ex.CorrelationId = context.CorrelationId.ToString("D");
            }

            throw ErrorTranslator.ToPluginException(ex);
        }
    }
}

/// <summary>
/// Phase 0 spike: RetrieveMultiple returns one hard-coded row, to prove provider registration before anything else.
/// Assumes the table's primary name column is &lt;prefix&gt;_name. Delete once phase 0 is written up.
/// </summary>
public sealed class SpikeRetrieveMultiplePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
        var table = context.PrimaryEntityName;
        var prefix = table.IndexOf('_') > 0 ? table.Substring(0, table.IndexOf('_')) : table;

        var id = new Guid("b1000000-0000-0000-0000-00000000002a");
        var row = new Entity(table, id);
        row[table + "id"] = id;
        row[prefix + "_name"] = "BrokerForge spike row";

        var rows = new EntityCollection { EntityName = table };
        rows.Entities.Add(row);
        context.OutputParameters["BusinessEntityCollection"] = rows;
    }
}
