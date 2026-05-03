using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace ApiVersioning.Tests;

[Collection(nameof(BackendServiceSmokeCollection))]
public class BackendServiceSmokeTests
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    public static IEnumerable<object[]> Services()
    {
        yield return ["Gateway", "src/Services/Gateway/Gateway.csproj"];
        yield return ["OrderService", "src/Services/OrderService/OrderService.csproj"];
        yield return ["PaymentService", "src/Services/PaymentService/PaymentService.csproj"];
        yield return ["ProductService", "src/Services/ProductService/ProductService.csproj"];
        yield return ["CartService", "src/Services/CartService/CartService.csproj"];
        yield return ["UserService", "src/Services/UserService/UserService.csproj"];
        yield return ["InventoryService", "src/Services/InventoryService/InventoryService.csproj"];
        yield return ["ShippingService", "src/Services/ShippingService/ShippingService.csproj"];
        yield return ["HistoryService", "src/Services/HistoryService/HistoryService.csproj"];
        yield return ["EmailService", "src/Services/EmailService/EmailService.csproj"];
    }

    [Theory]
    [MemberData(nameof(Services))]
    public async Task Service_HealthEndpoints_WorkWithAndWithoutVersionPrefix(string serviceName, string projectPath)
    {
        var port = serviceName == "InventoryService" ? 8080 : GetFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";
        using var process = StartService(projectPath, baseUrl);

        try
        {
            await WaitForHealthyAsync(baseUrl, TimeSpan.FromSeconds(120));

            using var unversioned = await HttpClient.GetAsync($"{baseUrl}/health");
            Assert.Equal(HttpStatusCode.OK, unversioned.StatusCode);
            Assert.Equal("v1", unversioned.Headers.GetValues("api-supported-versions").Single());

            using var versioned = await HttpClient.GetAsync($"{baseUrl}/api/v1/health");
            Assert.Equal(HttpStatusCode.OK, versioned.StatusCode);
            Assert.Equal("v1", versioned.Headers.GetValues("api-supported-versions").Single());
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException($"Smoke test failed for {serviceName}: {ex.Message}");
        }
        finally
        {
            StopProcess(process);
        }
    }

    private static Process StartService(string projectPath, string baseUrl)
    {
        var repoRoot = GetRepositoryRoot();
        var fullProjectPath = Path.Combine(repoRoot, projectPath);
        var projectDirectory = Path.GetDirectoryName(fullProjectPath)
            ?? throw new DirectoryNotFoundException($"Could not resolve directory for project '{projectPath}'.");
        var projectName = Path.GetFileNameWithoutExtension(fullProjectPath);
        var outputDllPath = Path.Combine(projectDirectory, "bin", "Debug", "net10.0", $"{projectName}.dll");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{outputDllPath}\" --urls \"{baseUrl}\"",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = Process.Start(psi);
        if (process is null)
        {
            throw new InvalidOperationException($"Failed to start dotnet process for '{projectPath}'.");
        }

        return process;
    }

    private static async Task WaitForHealthyAsync(string baseUrl, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await HttpClient.GetAsync($"{baseUrl}/health");
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch
            {
                // Ignore transient boot timing issues.
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Service did not become healthy within {timeout.TotalSeconds} seconds.");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string GetRepositoryRoot()
    {
        var path = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(path))
        {
            var candidate = Path.Combine(path, "OnlineStore.sln");
            if (File.Exists(candidate))
            {
                return path;
            }

            path = Directory.GetParent(path)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Could not find repository root containing OnlineStore.sln.");
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}

[CollectionDefinition(nameof(BackendServiceSmokeCollection), DisableParallelization = true)]
public class BackendServiceSmokeCollection;
