using bio_middleware.Services;

var builder = WebApplication.CreateBuilder(args);

// Habilitar soporte para Servicio de Windows
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "BioMiddlewareService";
});

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Registrar el servicio de descubrimiento en segundo plano
builder.Services.AddHostedService<BioDiscoveryService>();

var app = builder.Build();

app.UseCors();

app.MapGet("/", () => "U.are.U 5160 Local Biometric Bridge is running.");

app.MapGet("/api/status", () => 
{
    try 
    {
        return Results.Ok(new { message = BioService.GetStatus() });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapGet("/api/capture", async (HttpContext context) => 
{
    try 
    {
        // Pasamos el token de cancelación para detectar si el usuario cierra la conexión
        var result = await BioService.CaptureFingerprintAsync(context.RequestAborted);
        return Results.Ok(result);
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(499); // Client Closed Request
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/api/verify-legacy", async (VerifyRequest req, HttpContext context) => 
{
    try 
    {
        if (req.Templates == null || req.Templates.Count == 0)
        {
            return Results.BadRequest(new { success = false, message = "No se proporcionaron imágenes PNG para comparar." });
        }

        var result = await BioService.VerifyFingerprintFromPngAsync(req.Templates, context.RequestAborted);
        return Results.Ok(result);
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(499);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/api/verify", async (VerifyRequest req, HttpContext context) => 
{
    try 
    {
        if (req.Templates == null || req.Templates.Count == 0)
        {
            return Results.BadRequest(new { success = false, message = "No se proporcionaron templates para comparar." });
        }

        var result = await BioService.VerifyFingerprintAsync(req.Templates, context.RequestAborted);
        return Results.Ok(result);
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(499);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/api/enroll", async (VerifyRequest req, HttpContext context) => 
{
    try 
    {
        if (req.Templates == null || req.Templates.Count < 4)
        {
            return Results.BadRequest(new { success = false, message = "Se requieren 4 templates (capturas) para el enrolamiento." });
        }

        var result = await BioService.EnrollFingerprintAsync(req.Templates, context.RequestAborted);
        return Results.Ok(result);
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(499);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// Run on port 8081
app.Run("http://localhost:8081");

public class VerifyRequest
{
    public List<string> Templates { get; set; } = new List<string>();
}
