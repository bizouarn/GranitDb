using GranitDB.API.Connectors;
using GranitDB.API.Data;
using GranitDB.API.Options;
using GranitDB.API.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CatalogOptions>(builder.Configuration.GetSection(CatalogOptions.Section));

// Catalogue : SQLite par défaut (aucun serveur requis), SQL Server en option.
builder.Services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
builder.Services.AddSingleton<ICatalogRepository, CatalogRepository>();
builder.Services.AddSingleton<ISchemaConnector, SqliteSchemaConnector>();
builder.Services.AddSingleton<ISchemaConnector, SqlServerSchemaConnector>();
// Les connexions des sources sont chiffrées dans le catalogue ; les clés vivent dans un dossier à part.
var catalogSection = builder.Configuration.GetSection(CatalogOptions.Section).Get<CatalogOptions>() ?? new CatalogOptions();
builder.Services.AddDataProtection().SetApplicationName("GranitDb")
    .PersistKeysToFileSystem(new DirectoryInfo(catalogSection.ResolveKeysDirectory()));
builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
builder.Services.AddSingleton<SchemaSynchronizer>();
builder.Services.AddSingleton<DatabaseImportService>();

var corsPolicy = "_myAllowSpecificOrigins";
builder.Services.AddCors(options =>
{
    options.AddPolicy(corsPolicy,
        policy =>
        {
            policy.SetIsOriginAllowed(_ => true) // Autorise toutes les origines
                .AllowAnyMethod()
                .AllowAnyHeader()
                .WithExposedHeaders("X-Total-Count");
        });
});
builder.Services.AddControllers();
builder.Services.AddExceptionHandler<GranitExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Création du catalogue (fichier SQLite compris) et synchronisation des sources déclarées.
var catalogOptions = app.Services.GetRequiredService<IOptions<CatalogOptions>>().Value;
await app.Services.GetRequiredService<ICatalogRepository>().InitializeAsync();
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
startupLogger.LogInformation("Catalogue {Provider} prêt (dossier de données : {Dir})", catalogOptions.Provider,
    Path.GetFullPath(catalogOptions.DataDirectory));
var importer = app.Services.GetRequiredService<DatabaseImportService>();
foreach (var source in catalogOptions.StartupSources)
    try
    {
        var result = await importer.SyncSourceAsync(source);
        startupLogger.LogInformation("Source {Id} synchronisée : {Tables} tables ajoutées, {Columns} colonnes ajoutées",
            source.Id, result.TablesAdded, result.ColumnsAdded);
    }
    catch (Exception e)
    {
        startupLogger.LogError(e, "Synchronisation de la source {Id} impossible", source.Id);
    }

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors(corsPolicy);

// En conteneur (HTTP seul derrière un proxy éventuel) il n'y a pas de port HTTPS vers lequel rediriger.
if (app.Configuration["ASPNETCORE_URLS"]?.Contains("https", StringComparison.OrdinalIgnoreCase) == true
    || app.Configuration["ASPNETCORE_HTTPS_PORT"] != null
    || app.Configuration["HTTPS_PORT"] != null
    || app.Configuration["ASPNETCORE_HTTPS_PORTS"] != null)
    app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();

/// <summary>Exposé pour les tests d'intégration (WebApplicationFactory).</summary>
public partial class Program
{
}
