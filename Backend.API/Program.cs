using Core.Logging;
using Backend.API.Services;
using Backend.API.Startup;
using DotNetEnv;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;

// Setup global unhandled exception logger for crash.log
AppDomain.CurrentDomain.UnhandledException += (s, e) =>
{
    if (e.ExceptionObject is Exception ex)
    {
        AppLogger.LogCrash(ex, "AppDomain.UnhandledException");
    }
};

try
{
    AppLogger.LogStart("Backend API initialization starting...");

    var builder = WebApplication.CreateBuilder(args);

    // 8U-M2: archivo de secretos protegido (secrets.json con ACL restrictiva creado por el
    // instalador). Se carga con máxima precedencia (al final de la cadena) y ANTES de
    // LoadHttpsCertificate, evitando exponer credenciales por línea de comandos
    // (AppEnvironmentExtra de NSSM / administrador de tareas).
    try
    {
        var secretsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "secrets.json");
        if (File.Exists(secretsPath))
        {
            builder.Configuration.AddJsonFile(secretsPath, optional: false, reloadOnChange: false);
            AppLogger.LogStart("[8U-M2] Secretos cargados desde secrets.json (ACL restrictiva).");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[STARTUP] Aviso: no se pudo cargar secrets.json: {ex.Message}");
    }

    // HTTPS (contexto seguro requerido por el escáner de cámara desde dispositivos de la red local).
    // Intenta cargar el certificado HTTPS autofirmado para escuchar también en el puerto 5001.
    // Si el certificado falta, el servidor continúa sirviendo solo HTTP sin romper el arranque.
    var httpsCert = Backend.API.Startup.BackendHelpers.LoadHttpsCertificate(builder.Configuration, builder.Environment);

    // Limpia la configuración de 'urls' para evitar la advertencia de Kestrel (Overriding address(es)) al definir ListenAnyIP.
    builder.Configuration["urls"] = null;

    // 8.7-L2: AllowedHosts restringido — sin "*". Se permite localhost, el nombre del equipo y las
    // IPs locales de la máquina (LAN POS), preservando entradas explícitas de configuración/env.
    var rawAllowedHosts = builder.Configuration["AllowedHosts"] ?? "localhost";
    var computedAllowedHosts = string.IsNullOrWhiteSpace(rawAllowedHosts) || rawAllowedHosts == "*"
        ? Backend.API.Startup.BackendHelpers.BuildLanAllowedHosts()
        : Backend.API.Startup.BackendHelpers.BuildLanAllowedHosts() + ";" + rawAllowedHosts;
    builder.Configuration["AllowedHosts"] = computedAllowedHosts;

    int httpPort = int.TryParse(Environment.GetEnvironmentVariable("PORT") ?? Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORT"), out int p) ? p : 5000;
    int httpsPort = httpPort + 1;

    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.ListenAnyIP(httpPort);
        if (httpsCert != null)
        {
            kestrel.ListenAnyIP(httpsPort, listen => listen.UseHttps(httpsCert));
        }
    });

    if (httpsCert != null)
    {
        AppLogger.LogStart("HTTPS habilitado en https://0.0.0.0:5001 (certificado autofirmado)");
    }
    else
    {
        // 8.9-M7: en Producción un arranque sin HTTPS es un fallo de seguridad, no una advertencia.
        if (builder.Environment.IsProduction())
        {
            AppLogger.LogCrash(
                new InvalidOperationException(
                    "[FATAL] HTTPS no disponible en Producción. No se puede iniciar el backend sin canal cifrado. Configure HTTPS_CERT_THUMBPRINT (Windows Store) o genere el pfx por sitio (scripts/create-https-cert.ps1) y provea su HTTPS_CERT_PASSWORD."),
                "Backend.API.Program.Startup.HttpsRequired");
            throw new InvalidOperationException(
                "[FATAL] HTTPS no disponible en Producción. El backend se niega a arrancar sin canal cifrado. Consulte el log.");
        }

        AppLogger.LogStart("[AVISO] Certificado HTTPS no encontrado; el servidor solo escuchará en http://0.0.0.0:5000. Ejecute scripts/create-https-cert.ps1 para habilitar HTTPS.");
    }

    // Enable Windows Service integration (allows sc.exe to manage service natively without Error 1053)
    builder.Host.UseWindowsService();

    // 8.29-B4: carga de .env SOLO en Desarrollo. En Producción/Stage no se recorre el
    // directorio base en busca de .env: evita que un .env residual de un publish/build
    // viejos (o una carpeta de trabajo) inyecte conexiones/secretos en producción.
    if (builder.Environment.IsDevelopment())
    {
        try
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                var envCandidate = Path.Combine(dir.FullName, ".env");
                if (File.Exists(envCandidate))
                {
                    Env.Load(envCandidate);
                    break;
                }
                dir = dir.Parent;
            }
        }
        catch (Exception ex)
        {
            // 8B-B4: no dejar el catch vacío; registrar el fallo del recorrido de directorios.
            Console.WriteLine($"[STARTUP] Aviso: no se pudo recorrer el directorio base en busca de configuracion: {ex.Message}");
        }
    }

    // Prioritize Connection String from appsettings.json / appsettings.Production.json
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    var dbPasswordEnv = Environment.GetEnvironmentVariable("DB_PASSWORD");

    if (!string.IsNullOrEmpty(connectionString))
    {
        var csb = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.IsNullOrEmpty(dbPasswordEnv))
        {
            csb.Password = dbPasswordEnv;
        }
        connectionString = csb.ToString();
        AppLogger.LogStart($"DB Connection configured: Host={csb.Host};Port={csb.Port};Database={csb.Database};Username={csb.Username}");
    }

    // Registro de servicios del backend (DI, EF, JWT, CORS, rate limiting, etc.)
    builder.AddBackendServices(connectionString);

    var app = builder.Build();

    // Composición del pipeline HTTP
    app.ConfigureBackendPipeline(httpsCert, httpsPort);

    // Ensure Database Exists, Migrations and Seed Data (fail-fast: abort startup on any failure)
    if (!await Backend.API.Startup.DatabaseInitializer.InitializeAsync(app, connectionString))
    {
        return; // NO seguir arrancando: evita servir peticiones con estado inconsistente
    }

    AppLogger.LogStart("Backend API started successfully listening on configured ports.");
    app.Run();
}
catch (Exception fatalEx)
{
    AppLogger.LogCrash(fatalEx, "Backend.API.Program.FatalStartup");
    throw;
}

// 8.6-B6: expone Program para WebApplicationFactory<Program> (clase pública requerida por el
// integrador de hosting de pruebas sin reescribir el entrypoint de producción).
public partial class Program { }
