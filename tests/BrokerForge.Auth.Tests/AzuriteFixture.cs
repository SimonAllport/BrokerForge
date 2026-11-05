using System.Net.Sockets;

namespace BrokerForge.Auth.Tests;

/// <summary>
/// Azurite's table endpoint on 127.0.0.1:10002: used if already running, otherwise started with npx for the
/// test run (Node is already needed for the mock API). If it cannot start, the tests fail rather than skip.
/// </summary>
public sealed class AzuriteFixture : IDisposable
{
    // Azurite's published development account; not a secret.
    public const string ConnectionString = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;TableEndpoint=http://127.0.0.1:10002/devstoreaccount1;";
    private readonly System.Diagnostics.Process? _azurite;

    public AzuriteFixture()
    {
        if (Reachable())
            return;
        var info = new System.Diagnostics.ProcessStartInfo("npx",
            "--yes azurite@3.33.0 azurite-table --tableHost 127.0.0.1 --tablePort 10002 --inMemoryPersistence --skipApiVersionCheck --silent")
            { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetTempPath() };
        _azurite = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("Could not start npx to run Azurite.");
        _azurite.OutputDataReceived += (_, _) => { };
        _azurite.ErrorDataReceived += (_, _) => { };
        _azurite.BeginOutputReadLine();
        _azurite.BeginErrorReadLine();
        var until = DateTime.UtcNow.AddSeconds(90);
        while (!Reachable())
        {
            if (_azurite.HasExited)
                throw new InvalidOperationException($"Azurite exited with code {_azurite.ExitCode} before listening on 127.0.0.1:10002.");
            if (DateTime.UtcNow > until)
                throw new TimeoutException("Azurite did not listen on 127.0.0.1:10002 within 90 seconds.");
            Thread.Sleep(250);
        }
    }

    private static bool Reachable()
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", 10002).Wait(TimeSpan.FromMilliseconds(500)) && client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_azurite is { HasExited: false })
            _azurite.Kill(entireProcessTree: true);
        _azurite?.Dispose();
    }
}

/// <summary>
/// One Azurite for every table test class. With a fixture per class, the class that started Azurite killed it when it
/// finished, while another class that had found it running was still using it.
/// </summary>
[CollectionDefinition(Name)]
public sealed class AzuriteCollection : ICollectionFixture<AzuriteFixture>
{
    public const string Name = "Azurite";
}
