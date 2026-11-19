using System.ComponentModel;
using System.Diagnostics;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Discovery.IntegrationTests.Fixtures;

[SetUpFixture]
public sealed class IntegrationTestAssemblySetup
{
    [OneTimeTearDown]
    public async Task GlobalTeardownAsync()
    {
        await PostgresContainerFixture.DisposeSharedContainerAsync();
    }
}

/// <summary>
/// Manages a shared Docker-gated PostgreSQL (pgvector) Testcontainer for integration tests.
/// Probes Docker availability with a short timeout; when Docker is absent or unreachable locally,
/// tests are cleanly skipped via Assert.Ignore instead of hanging or failing.
/// </summary>
public static class PostgresContainerFixture
{
    public const string PostgresImage = "pgvector/pgvector:pg17";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Lazy<Task<(bool Available, string Reason)>> DockerProbe = new(ProbeDockerAvailabilityAsync);
    private static PostgreSqlContainer? _sharedContainer;

    public static async Task EnsureAvailableOrIgnoreAsync()
    {
        var requireDocker = string.Equals(
            Environment.GetEnvironmentVariable("DISCOVERY_INTEGRATION_REQUIRE_DOCKER"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        var (available, reason) = await DockerProbe.Value;
        if (!available)
        {
            if (requireDocker)
            {
                Assert.Fail($"Docker daemon is required (DISCOVERY_INTEGRATION_REQUIRE_DOCKER=true) but unavailable: {reason}");
            }

            Assert.Ignore($"Docker daemon unavailable — skipping PostgreSQL integration test. ({reason})");
        }

        await Gate.WaitAsync();
        try
        {
            if (_sharedContainer is not null)
                return;

            var container = new PostgreSqlBuilder(PostgresImage)
                .WithDatabase("postgres")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await container.StartAsync(cts.Token);
            _sharedContainer = container;
        }
        catch (Exception ex) when (!requireDocker)
        {
            Assert.Ignore($"Docker daemon unavailable — skipping PostgreSQL integration test. ({ex.GetType().Name}: {ex.Message})");
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<(string ConnectionString, string DatabaseName)> CreateIsolatedDatabaseAsync(string prefix)
    {
        await EnsureAvailableOrIgnoreAsync();

        var adminConnectionString = _sharedContainer!.GetConnectionString();
        var sanitizedPrefix = new string(prefix.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (string.IsNullOrEmpty(sanitizedPrefix))
            sanitizedPrefix = "db";

        var databaseName = $"discovery_it_{sanitizedPrefix}_{Guid.NewGuid():N}";

        await using var conn = new NpgsqlConnection(adminConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""CREATE DATABASE "{databaseName}";""";
        await cmd.ExecuteNonQueryAsync();

        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = databaseName
        };

        return (builder.ConnectionString, databaseName);
    }

    public static async Task DropDatabaseAsync(string databaseName)
    {
        if (_sharedContainer is null || string.IsNullOrWhiteSpace(databaseName))
            return;

        NpgsqlConnection.ClearAllPools();

        try
        {
            await using var conn = new NpgsqlConnection(_sharedContainer.GetConnectionString());
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""DROP DATABASE IF EXISTS "{databaseName}" WITH (FORCE);""";
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best-effort cleanup; container itself is disposed at assembly teardown.
        }
    }

    public static async Task DisposeSharedContainerAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_sharedContainer is not null)
            {
                await _sharedContainer.DisposeAsync();
                _sharedContainer = null;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<(bool Available, string Reason)> ProbeDockerAvailabilityAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "info --format {{.ServerVersion}}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
                return (false, "Failed to launch 'docker info'.");

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2500));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (false, "'docker info' timed out after 2500ms.");
            }

            if (process.ExitCode != 0)
            {
                var stderr = (await process.StandardError.ReadToEndAsync()).Trim();
                return (false, string.IsNullOrWhiteSpace(stderr)
                    ? $"'docker info' exited with code {process.ExitCode}."
                    : stderr);
            }

            var version = (await process.StandardOutput.ReadToEndAsync()).Trim();
            return (true, $"Docker {version}");
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            return (false, "'docker' CLI not found on PATH.");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
