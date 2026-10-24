using Azure.Core;
using Azure.Identity;
using BrokerForge.Abstractions;
using BrokerForge.Adapters.Rest;
using BrokerForge.Auth;
using BrokerForge.Auth.AzureIdentity;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Execution;
using BrokerForge.Descriptors.Storage;

namespace BrokerForge.Host;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBrokerForgeHost(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var options = configuration.GetSection("BrokerForge").Get<BrokerForgeOptions>() ?? new BrokerForgeOptions();
        services.AddSingleton(options);

        // Trust: fail closed by default; the development bypass cannot run outside Development.
        switch (options.Trust.Mode)
        {
            case "ManagedIdentity":
                services.AddSingleton<IRequestTrust, ManagedIdentityRequestTrust>();
                break;
            case "Development" when environment.IsDevelopment():
                services.AddSingleton<IRequestTrust, DevelopmentRequestTrust>();
                break;
            case "Development":
                throw new InvalidOperationException("Trust mode 'Development' is only allowed when ASPNETCORE_ENVIRONMENT=Development.");
            default:
                throw new InvalidOperationException($"Unknown trust mode '{options.Trust.Mode}'. Use ManagedIdentity or Development.");
        }

        // Configuration and descriptors: file/appsettings-backed until the Dataverse stores land (phase 1).
        services.AddSingleton<IConfigurationStore>(new OptionsConfigurationStore(options));
        var descriptorRoot = Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.DescriptorRoot));
        services.AddSingleton<IDescriptorStore>(_ => new FileDescriptorStore(descriptorRoot));

        // Secrets and authentication. No provider falls back to another mode.
        services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
        services.AddSingleton<ISecretResolver>(sp =>
        {
            var schemes = new Dictionary<string, ISecretResolver>(StringComparer.Ordinal)
            {
                ["kv"] = new KeyVaultSecretResolver(sp.GetRequiredService<TokenCredential>()),
            };

            if (environment.IsDevelopment())
            {
                schemes["env"] = new EnvironmentSecretResolver();
            }

            return new SecretResolver(schemes);
        });
        services.AddSingleton<ITokenSource>(sp => new AzureTokenSource(sp.GetRequiredService<TokenCredential>()));
        services.AddSingleton<IAuthProvider, ServiceAccountAuthProvider>();
        services.AddSingleton<IAuthProvider, StaticAuthProvider>();
        services.AddSingleton<IAuthProvider, ImpersonateAuthProvider>();
        services.AddSingleton<IAuthProvider, SsoAuthProvider>();
        services.AddSingleton<IAuthProvider, OAuthAuthProvider>();
        services.AddSingleton<AuthProviderRegistry>();

        // Adapters.
        services.AddHttpClient<IBroker, RestBroker>(client => client.Timeout = Timeout.InfiniteTimeSpan);

        // Descriptor engine and router.
        services.AddSingleton<IRequestRenderer, RequestRenderer>();
        services.AddSingleton<IResponseMapper, ResponseMapper>();
        services.AddScoped<BrokerRouter>();

        return services;
    }
}
