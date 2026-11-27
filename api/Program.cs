using GranitDB.API.Connectors;
using GranitDB.API.Data;
using GranitDB.API.Options;
using GranitDB.API.Security;
using GranitDB.API.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CatalogOptions>(builder.Configuration.GetSection(CatalogOptions.Section));

// Catalogue : SQLite par défaut (aucun serveur requis), SQL Server en option.
builder.Services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
builder.Services.AddSingleton<ICatalogRepository, CatalogRepository>();
builder.Services.AddSingleton<ISchemaConnector, SqliteSchemaConnector>();
builder.Services.AddSingleton<ISchemaConnector, H2SchemaConnector>();
builder.Services.AddSingleton<ISchemaConnector, SqlServerSchemaConnector>();
builder.Services.AddSingleton<ISchemaConnector, PostgreSqlSchemaConnector>();
builder.Services.AddSingleton<ISchemaConnector, MySqlSchemaConnector>();
builder.Services.AddSingleton<IColumnProfiler, PostgreSqlColumnProfiler>();
builder.Services.AddSingleton<IColumnProfiler, MySqlColumnProfiler>();
builder.Services.AddSingleton<IColumnProfiler, SqliteColumnProfiler>();
builder.Services.AddSingleton<IColumnProfiler, SqlServerColumnProfiler>();
builder.Services.AddSingleton<ColumnProfilingService>();
builder.Services.AddSingleton<RelationDetectionService>();
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.Section));
builder.Services.AddSingleton<IAiProvider, OpenAiCompatibleProvider>();
builder.Services.AddSingleton<DescriptionAssistant>();
// Les connexions des sources sont chiffrées dans le catalogue ; les clés vivent dans un dossier à part.
var catalogSection = builder.Configuration.GetSection(CatalogOptions.Section).Get<CatalogOptions>() ?? new CatalogOptions();
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("GranitDb")
    .PersistKeysToFileSystem(new DirectoryInfo(catalogSection.ResolveKeysDirectory()));
// Les clés ne restent pas forcément en clair sur le disque : DPAPI (Windows) ou un certificat.
if (catalogSection.KeysProtection.Equals("Dpapi", StringComparison.OrdinalIgnoreCase))
{
    if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Catalog:KeysProtection = Dpapi n'existe que sous Windows : utilisez Certificate.");
    dataProtection.ProtectKeysWithDpapi();
}
else if (catalogSection.KeysProtection.Equals("Certificate", StringComparison.OrdinalIgnoreCase))
{
    if (string.IsNullOrWhiteSpace(catalogSection.KeysCertificatePath))
        throw new InvalidOperationException("Catalog:KeysProtection = Certificate demande Catalog:KeysCertificatePath.");
    var certificatePassword = string.IsNullOrEmpty(catalogSection.KeysCertificatePassword)
        ? null
        : SecretReference.Resolve(catalogSection.KeysCertificatePassword);
    dataProtection.ProtectKeysWithCertificate(System.Security.Cryptography.X509Certificates.X509CertificateLoader
        .LoadPkcs12FromFile(catalogSection.KeysCertificatePath, certificatePassword));
}
else if (!catalogSection.KeysProtection.Equals("None", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException($"Catalog:KeysProtection inconnu : {catalogSection.KeysProtection} (None, Dpapi ou Certificate).");
builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
builder.Services.AddSingleton<SchemaSynchronizer>();
builder.Services.Configure<WebhookOptions>(builder.Configuration.GetSection(WebhookOptions.Section));
builder.Services.Configure<AuditOptions>(builder.Configuration.GetSection(AuditOptions.Section));
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<WebhookStore>();
builder.Services.AddSingleton<WebhookDispatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebhookDispatcher>());
builder.Services.AddSingleton<DatabaseImportService>();
builder.Services.AddSingleton<SyncScheduler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncScheduler>());

// Authentification (GitHub, OpenID Connect) et rôles : inactifs tant que Auth:Enabled n'est pas vrai.
builder.Services.AddGranitAuth(builder.Configuration);
builder.Services.AddSingleton<PublicAccess>();
builder.Services.Configure<CorsSettings>(builder.Configuration.GetSection(CorsSettings.Section));
var allowedOrigins = (builder.Configuration.GetSection(CorsSettings.Section).Get<CorsSettings>() ?? new CorsSettings())
    .AllowedOrigins.Select(o => o.Trim().TrimEnd('/')).Where(o => o.Length > 0).ToArray();

var corsPolicy = "_myAllowSpecificOrigins";
builder.Services.AddCors(options =>
{
    options.AddPolicy(corsPolicy,
        policy =>
        {
            // Origines déclarées : avec les cookies de session. Aucune : toutes les origines, sans cookies (développement).
            if (allowedOrigins.Length > 0) policy.WithOrigins(allowedOrigins).AllowCredentials();
            else policy.SetIsOriginAllowed(_ => true);
            policy.AllowAnyMethod()
                .AllowAnyHeader()
                .WithExposedHeaders("X-Total-Count");
        });
});
builder.Services.AddControllers(options => options.Filters.Add<RoleAuthorizationFilter>());
// Un envoi de fichier est un formulaire « multipart », plafonné par ASP.NET à 128 Mo quel que soit [RequestSizeLimit] : ce plafond
// suit Catalog:MaxUploadMb (avec une marge pour les autres champs), sinon les gros fichiers sont refusés avant d'arriver au contrôle.
builder.Services.AddOptions<Microsoft.AspNetCore.Http.Features.FormOptions>()
    .Configure<IOptions<CatalogOptions>>((form, catalog) =>
        form.MultipartBodyLengthLimit = (catalog.Value.MaxUploadMb + 1L) * 1024 * 1024);
builder.Services.AddExceptionHandler<GranitExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // string = jamais null, string? = peut l'être : le client généré garde cette distinction.
    options.SupportNonNullableReferenceTypes();
    // Seules les routes versionnées sont décrites (l'alias sans préfixe doublerait chaque opération) ; chaque opération a un
    // nom stable « Contrôleur_Action » : c'est lui qui nomme les méthodes du client typé généré (client/api/openapi.json).
    options.DocInclusionPredicate((_, api) => api.RelativePath?.StartsWith("api/v1/", StringComparison.Ordinal) == true);
    options.CustomOperationIds(api =>
    {
        // Les actions génériques du contrôleur de base finissent par « T » (GetAllT, GetT…) : on retire cette trace technique.
        var action = api.ActionDescriptor.RouteValues["action"] ?? "";
        if (action is "GetAllT" or "GetT" or "CreateT" or "UpdateT" or "DeleteT") action = action[..^1];
        return $"{api.ActionDescriptor.RouteValues["controller"]}_{action}";
    });
    // Les actions renvoient IActionResult : ce filtre déclare le type de chaque réponse pour le client généré.
    options.OperationFilter<GranitDB.API.Swagger.ResponseTypesFilter>();
});

var app = builder.Build();

var authOptions = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
if (authOptions.Enabled && allowedOrigins.Length == 0)
    app.Logger.LogWarning("Auth:Enabled sans Cors:AllowedOrigins : seule l'origine de l'API peut ouvrir une session. " +
                          "Déclarez l'adresse de l'interface dans Cors:AllowedOrigins.");
// Création du catalogue (fichier SQLite compris) et synchronisation des sources déclarées.
var catalogOptions = app.Services.GetRequiredService<IOptions<CatalogOptions>>().Value;
await app.Services.GetRequiredService<ICatalogRepository>().InitializeAsync();
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
startupLogger.LogInformation("Catalogue {Provider} prêt (dossier de données : {Dir})", catalogOptions.Provider,
    Path.GetFullPath(catalogOptions.DataDirectory));
var importer = app.Services.GetRequiredService<DatabaseImportService>();
try
{
    var orphans = await importer.CleanOrphanFilesAsync();
    if (orphans > 0) startupLogger.LogInformation("{Count} fichier(s) source orphelin(s) supprimé(s)", orphans);
}
catch (Exception e)
{
    startupLogger.LogWarning(e, "Balayage des fichiers sources orphelins impossible");
}
foreach (var source in catalogOptions.StartupSources)
    try
    {
        if (source.Type.Equals("SqlServer", StringComparison.OrdinalIgnoreCase) && !SecretReference.IsReference(source.Source) &&
            source.Source.Contains("password", StringComparison.OrdinalIgnoreCase))
            startupLogger.LogWarning("La source {Id} garde son mot de passe en clair dans la configuration : utilisez env:NOM ou file:/chemin.",
                source.Id);
        var result = await importer.SyncSourceAsync(source);
        startupLogger.LogInformation("Source {Id} synchronisée : {Tables} tables ajoutées, {Columns} colonnes ajoutées",
            source.Id, result.TablesAdded, result.ColumnsAdded);
    }
    catch (Exception e)
    {
        startupLogger.LogError(e, "Synchronisation de la source {Id} impossible", source.Id);
    }

// Avant le gestionnaire d'erreurs : le journal voit le code de réponse définitif.
// Chaque réponse porte l'identifiant de la requête (le même que le traceId des erreurs et des journaux) et des en-têtes
// de durcissement ; l'API ne sert que du JSON, jamais de page : rien ne doit être interprété ni mis en cadre.
app.Use(async (context, next) =>
{
    var id = context.TraceIdentifier;
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers["X-Request-Id"] = id;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Resource-Policy"] = "cross-origin"; // le client est servi depuis une autre origine
        return Task.CompletedTask;
    });
    using (app.Logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = id }))
        await next();
});
// L'authentification précède le journal d'audit : l'acteur de chaque écriture est le compte connecté.
app.UseAuthentication();
app.UseMiddleware<AuditMiddleware>();
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
