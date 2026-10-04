using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using Npgsql;

namespace CommandCenter.Wpf.E2ETests.Fixtures;

public enum FullStackGatingDecision
{
    Proceed,
    Unavailable
}

/// <summary>
/// Política de gating del harness full-stack (pura y testeable): con la variable de conexión
/// presente se procede; ausente en local se retorna en silencio; ausente con marcador de CI se
/// falla cerrado para que una mala configuración del pipeline nunca pase como verde.
/// </summary>
public static class FullStackGating
{
    public const string ConnectionStringEnvironmentVariable = "E2E_POSTGRES_CONNECTION";
    public const string GitHubActionsEnvironmentVariable = "GITHUB_ACTIONS";

    public static FullStackGatingDecision Evaluate(string? connectionString, string? gitHubActions)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return FullStackGatingDecision.Proceed;
        }

        if (string.Equals(gitHubActions?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{ConnectionStringEnvironmentVariable} no está definida y el entorno es CI ({GitHubActionsEnvironmentVariable}=true). " +
                "El harness full-stack falla cerrado: configure la variable con la cadena de conexión PostgreSQL base " +
                "(Host/Port/Username/Password); el fixture deriva de ahí la base aislada pos_e2e_<run>.");
        }

        return FullStackGatingDecision.Unavailable;
    }
}

/// <summary>
/// Harness E2E full-stack: levanta <c>Backend.API</c> real contra una base PostgreSQL aislada
/// (<c>pos_e2e_&lt;run&gt;</c>) en un puerto libre, espera <c>/health</c> Healthy, prepara el estado por
/// la API real (rota la clave forzada del admin sembrado, fija la tasa y crea un producto de prueba),
/// neutraliza el settings persistido del cliente (backup/restore) y lanza <c>Desktop.Client.exe</c>
/// SIN <c>--e2e</c> apuntado al backend por variable de entorno. El teardown es idempotente y corre
/// en <c>finally</c>: mata procesos, dropea la base y restaura el settings del equipo.
/// </summary>
public sealed class FullStackFixture : IDisposable
{
    /// <summary>Usuario/contraseña semilla que <c>DatabaseInitializer</c> siembra con MustChangePassword=true.</summary>
    public const string SeedUsername = "admin";
    public const string SeedPassword = "E2eSeed!2026";

    /// <summary>Clave rotada por el bootstrap (pasa la política de contraseñas) y usada por la UI real.</summary>
    public const string RotatedPassword = "E2eRotated!2026";

    public const decimal BootstrapExchangeRate = 50.00m;

    private const string ClientSettingsRelativePath = "ProyectoPOS\\client_settings.json";
    private const int HealthTimeoutSeconds = 90;
    private const int BackendOutputTailChars = 8000;

    private readonly string? _baseConnectionString;
    private readonly string _backendBaseAddress;
    private readonly string _derivedConnectionString = string.Empty;
    private readonly StringBuilder _backendOutput = new();
    private readonly object _backendOutputLock = new();
    private readonly List<string> _teardownErrors = new();

    private Process? _backendProcess;
    private WpfAppFixture? _clientFixture;
    private bool _databaseCreated;
    private bool _initialized;
    private bool _teardownCompleted;

    public FullStackFixture()
    {
        _baseConnectionString = Environment.GetEnvironmentVariable(FullStackGating.ConnectionStringEnvironmentVariable);

        IsAvailable = FullStackGating.Evaluate(
            _baseConnectionString,
            Environment.GetEnvironmentVariable(FullStackGating.GitHubActionsEnvironmentVariable)) == FullStackGatingDecision.Proceed;

        BackendPort = FindFreePort();
        _backendBaseAddress = $"http://127.0.0.1:{BackendPort}/";
        ClientSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ClientSettingsRelativePath);

        if (IsAvailable)
        {
            DatabaseName = BuildDatabaseName();
            _derivedConnectionString = BuildDerivedConnectionString(_baseConnectionString!, DatabaseName);
        }
    }

    public bool IsAvailable { get; }

    public int BackendPort { get; }

    public string? DatabaseName { get; }

    public string BackendBaseAddress => _backendBaseAddress;

    public string ClientSettingsPath { get; }

    /// <summary>Credenciales efectivas para la UI real: usuario semilla con la clave ya rotada.</summary>
    public string AdminUsername => SeedUsername;

    public string AdminPassword => RotatedPassword;

    public bool ClientSettingsExistedBefore { get; private set; }

    public string? ClientSettingsOriginalContent { get; private set; }

    /// <summary>true solo si el settings quedó exactamente como estaba antes de la corrida.</summary>
    public bool ClientSettingsRestored { get; private set; }

    public E2eBootstrapState? Bootstrap { get; private set; }

    public Window? ClientWindow { get; private set; }

    public Application? App => _clientFixture?.App;

    public IReadOnlyList<string> TeardownErrors => _teardownErrors;

    public static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || _initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            await CreateDatabaseAsync(cancellationToken);
            StartBackend();
            await WaitForBackendHealthyAsync(cancellationToken);
            await VerifyIsolatedDatabaseInitializedAsync(cancellationToken);
            Bootstrap = await BootstrapStateAsync(cancellationToken);
            NeutralizeClientSettings();
            LaunchClient();
        }
        catch
        {
            await TeardownAsync();
            throw;
        }
    }

    public async Task TeardownAsync()
    {
        if (_teardownCompleted)
        {
            return;
        }

        _teardownCompleted = true;

        StopClient();
        StopBackend();
        await DropDatabaseAsync();
        RestoreClientSettings();
    }

    public void Dispose()
    {
        try
        {
            TeardownAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _teardownErrors.Add($"dispose: {ex.Message}");
        }
    }

    public async Task<bool> DatabaseExistsAsync()
    {
        if (string.IsNullOrWhiteSpace(_baseConnectionString) || string.IsNullOrWhiteSpace(DatabaseName))
        {
            return false;
        }

        await using var connection = new NpgsqlConnection(BuildMaintenanceConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pg_database WHERE datname = @db";
        command.Parameters.AddWithValue("db", DatabaseName);
        return await command.ExecuteScalarAsync() != null;
    }

    /// <summary>Diálogos de error de la app (mismo criterio que E2EAppHealthTests).</summary>
    public string[] FindErrorDialogs()
    {
        if (_clientFixture?.App is not { } app)
        {
            return Array.Empty<string>();
        }

        return app.GetAllTopLevelWindows(_clientFixture.Automation)
            .Where(window => (window.Properties.AutomationId.ValueOrDefault ?? string.Empty)
                .StartsWith("CustomDialogWindow_Error", StringComparison.Ordinal))
            .Select(window => window.Name)
            .ToArray();
    }

    private static string BuildDatabaseName()
    {
        return $"pos_e2e_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}".Substring(0, 40).ToLowerInvariant();
    }

    private static string BuildDerivedConnectionString(string baseConnectionString, string databaseName)
    {
        try
        {
            return new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = databaseName }.ConnectionString;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{FullStackGating.ConnectionStringEnvironmentVariable} no es una cadena Npgsql válida: {ex.Message}", ex);
        }
    }

    private string BuildMaintenanceConnectionString()
    {
        // Conexión a nivel servidor (base de mantenimiento estándar) para CREATE/DROP DATABASE.
        return new NpgsqlConnectionStringBuilder(_baseConnectionString!) { Database = "postgres" }.ConnectionString;
    }

    private async Task CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(BuildMaintenanceConnectionString());
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"No se pudo conectar a PostgreSQL con {FullStackGating.ConnectionStringEnvironmentVariable} " +
                $"(Host={new NpgsqlConnectionStringBuilder(_baseConnectionString!).Host}). " +
                $"Verifique el servicio y las credenciales: {ex.Message}", ex);
        }

        await ExecuteNonQueryAsync(connection, $"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)", cancellationToken);
        await ExecuteNonQueryAsync(connection, $"CREATE DATABASE \"{DatabaseName}\"", cancellationToken);
        _databaseCreated = true;
    }

    private static async Task ExecuteNonQueryAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void StartBackend()
    {
        var executablePath = FindBackendExecutable();
        var startInfo = new ProcessStartInfo(executablePath)
        {
            WorkingDirectory = Path.GetDirectoryName(executablePath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        // Program.cs ignora ASPNETCORE_URLS (nullifica la key "urls") y arma Kestrel con
        // PORT/ASPNETCORE_HTTP_PORT; se setean ambos para que el puerto libre sea el efectivo.
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{BackendPort}";
        startInfo.Environment["ASPNETCORE_HTTP_PORT"] = BackendPort.ToString();
        startInfo.Environment["ConnectionStrings__DefaultConnection"] = _derivedConnectionString;
        startInfo.Environment["SystemSettings__AdminSeedPassword"] = SeedPassword;

        // En Development Program.cs carga Backend.API/.env (DotNetEnv) y ese archivo puede pisar
        // variables del proceso; se setea DB_PASSWORD con el valor de la cadena base para que la
        // contraseña efectiva coincida cuando el .env no la defina. La verificación de base aislada
        // posterior detecta cualquier override de ConnectionStrings__DefaultConnection.
        var basePassword = new NpgsqlConnectionStringBuilder(_baseConnectionString!).Password;
        if (!string.IsNullOrEmpty(basePassword))
        {
            startInfo.Environment["DB_PASSWORD"] = basePassword;
        }

        var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => AppendBackendOutput(e.Data);
        process.ErrorDataReceived += (_, e) => AppendBackendOutput(e.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException($"No se pudo iniciar el proceso del backend '{executablePath}'.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _backendProcess = process;
    }

    private static string FindBackendExecutable()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\..\Backend.API\bin\Release\net10.0\Backend.API.exe")),
            Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\..\Backend.API\bin\Debug\net10.0\Backend.API.exe"))
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "Backend.API.exe no encontrado. Compile la solución (Release) antes de correr el E2E full-stack. " +
            $"Rutas probadas: {string.Join(" | ", candidates)}");
    }

    private async Task WaitForBackendHealthyAsync(CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow.AddSeconds(HealthTimeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_backendProcess!.HasExited)
            {
                throw new InvalidOperationException(
                    $"Backend.API terminó con código {_backendProcess.ExitCode} antes de reportar salud.{Environment.NewLine}" +
                    $"Salida del backend:{Environment.NewLine}{GetBackendOutputTail()}");
            }

            try
            {
                using var response = await httpClient.GetAsync($"{_backendBaseAddress}health", cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    using var json = JsonDocument.Parse(body);
                    if (json.RootElement.TryGetProperty("status", out var status) &&
                        string.Equals(status.GetString(), "Healthy", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // El backend aún no escucha: seguir reintentando hasta el deadline.
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new InvalidOperationException(
            $"Timeout de {HealthTimeoutSeconds}s esperando /health Healthy en {_backendBaseAddress}.{Environment.NewLine}" +
            $"Salida del backend:{Environment.NewLine}{GetBackendOutputTail()}");
    }

    private async Task VerifyIsolatedDatabaseInitializedAsync(CancellationToken cancellationToken)
    {
        // Backend.API/.env (DotNetEnv) puede pisar ConnectionStrings__DefaultConnection en Development.
        // Si el backend migró otra base, health responde igual: esta verificación evita un falso verde
        // operando contra la base de desarrollo.
        await using var connection = new NpgsqlConnection(_derivedConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass('public.\"Users\"') IS NOT NULL";
        var initialized = (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);

        if (!initialized)
        {
            throw new InvalidOperationException(
                $"El backend respondió Healthy pero la base aislada '{DatabaseName}' no tiene el esquema migrado. " +
                "Posible override de ConnectionStrings__DefaultConnection en Backend.API\\.env (DotNetEnv pisa las " +
                "variables del proceso en Development).");
        }
    }

    private async Task<E2eBootstrapState> BootstrapStateAsync(CancellationToken cancellationToken)
    {
        using var api = new E2eApiClient(_backendBaseAddress);

        var login = await api.LoginAsync(SeedUsername, SeedPassword, cancellationToken);
        string token;
        if (!login.RequiresPasswordChange && !string.IsNullOrWhiteSpace(login.Token))
        {
            token = login.Token;
        }
        else
        {
            // El admin sembrado nace con MustChangePassword=true: rota la clave forzada por la API
            // real antes de cualquier login de UI (D4).
            await api.ChangePasswordAsync(SeedUsername, SeedPassword, RotatedPassword, cancellationToken);
            var reLogin = await api.LoginAsync(SeedUsername, RotatedPassword, cancellationToken);
            token = reLogin.Token ?? throw new InvalidOperationException(
                "El re-login del bootstrap no devolvió token tras rotar la contraseña forzada.");
        }

        await api.SetExchangeRateAsync(token, BootstrapExchangeRate, cancellationToken);
        var product = await api.CreateTestProductAsync(token, cancellationToken);
        return new E2eBootstrapState(product.Name, product.Sku, BootstrapExchangeRate);
    }

    private void NeutralizeClientSettings()
    {
        var directory = Path.GetDirectoryName(ClientSettingsPath)!;
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        ClientSettingsExistedBefore = File.Exists(ClientSettingsPath);
        ClientSettingsOriginalContent = ClientSettingsExistedBefore ? File.ReadAllText(ClientSettingsPath) : null;

        // 8.148-T2 (ajuste de campo): se ESCRIBE un settings persistido con la dirección del harness
        // en lugar de borrarlo. La lógica del cliente (App.xaml.cs:157-161) honra la dirección
        // persistida cuando NO es la default; esa precedencia es determinística y no depende del
        // fallback por variable de entorno, que en la máquina de desarrollo no sobrescribía el
        // appsettings del cliente (appsettings.json fija localhost:5000). El contenido original se
        // restaura en el teardown.
        File.WriteAllText(
            ClientSettingsPath,
            JsonSerializer.Serialize(
                new { ServerBaseAddress = _backendBaseAddress, AutoDiscoverOnFailure = false },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private void LaunchClient()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BackendSettings__BaseAddress"] = _backendBaseAddress,
            // 8.148-T2: permite lanzar el cliente real aunque haya otra instancia abierta en la
            // máquina de desarrollo (mutex global saltado solo en modo test; ver Program.cs).
            ["E2E_SKIP_SINGLE_INSTANCE"] = "1"
        };

        _clientFixture = new WpfAppFixture();
        try
        {
            ClientWindow = _clientFixture.Launch(environment: environment, arguments: string.Empty);
        }
        catch (Exception ex)
        {
            // El modo real (sin --e2e) usa un mutex GLOBAL de instancia única: si otra instancia
            // está abierta, el proceso sale sin ventana y FlaUI falla con un error críptico.
            var runningClients = Process.GetProcessesByName("Desktop.Client").Select(process => process.Id).ToArray();
            throw new InvalidOperationException(
                "El cliente WPF real no pudo iniciar con ventana. Causa probable: otra instancia de " +
                "Desktop.Client.exe en ejecución retiene el mutex de instancia única (el modo real no usa --e2e). " +
                $"Procesos Desktop.Client activos: {(runningClients.Length == 0 ? "ninguno" : string.Join(", ", runningClients))}. " +
                $"Detalle: {ex.Message}", ex);
        }
    }

    private void StopClient()
    {
        try
        {
            _clientFixture?.Dispose();
        }
        catch (Exception ex)
        {
            _teardownErrors.Add($"client dispose: {ex.Message}");
        }
        finally
        {
            _clientFixture = null;
            ClientWindow = null;
        }
    }

    private void StopBackend()
    {
        try
        {
            if (_backendProcess is { HasExited: false })
            {
                _backendProcess.Kill(entireProcessTree: true);
                _backendProcess.WaitForExit(10000);
            }
        }
        catch (Exception ex)
        {
            _teardownErrors.Add($"backend kill: {ex.Message}");
        }
        finally
        {
            _backendProcess?.Dispose();
            _backendProcess = null;
        }
    }

    private async Task DropDatabaseAsync()
    {
        if (!_databaseCreated || string.IsNullOrWhiteSpace(DatabaseName))
        {
            return;
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(BuildMaintenanceConnectionString());
                await connection.OpenAsync();
                await ExecuteNonQueryAsync(
                    connection,
                    $"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)",
                    CancellationToken.None);
                return;
            }
            catch (Exception ex)
            {
                if (attempt == 3)
                {
                    _teardownErrors.Add($"drop database '{DatabaseName}': {ex.Message}");
                }
                else
                {
                    await Task.Delay(500);
                }
            }
        }
    }

    private void RestoreClientSettings()
    {
        try
        {
            if (ClientSettingsExistedBefore)
            {
                File.WriteAllText(ClientSettingsPath, ClientSettingsOriginalContent ?? string.Empty);
                ClientSettingsRestored = File.ReadAllText(ClientSettingsPath) == (ClientSettingsOriginalContent ?? string.Empty);
            }
            else
            {
                if (File.Exists(ClientSettingsPath))
                {
                    File.Delete(ClientSettingsPath);
                }

                ClientSettingsRestored = !File.Exists(ClientSettingsPath);
            }
        }
        catch (Exception ex)
        {
            _teardownErrors.Add($"restore client settings: {ex.Message}");
        }
    }

    private void AppendBackendOutput(string? line)
    {
        if (line == null)
        {
            return;
        }

        lock (_backendOutputLock)
        {
            _backendOutput.AppendLine(line);
            if (_backendOutput.Length > BackendOutputTailChars * 3)
            {
                _backendOutput.Remove(0, _backendOutput.Length - BackendOutputTailChars * 2);
            }
        }
    }

    private string GetBackendOutputTail()
    {
        lock (_backendOutputLock)
        {
            var output = _backendOutput.ToString();
            return output.Length <= BackendOutputTailChars
                ? output
                : output[^BackendOutputTailChars..];
        }
    }
}
