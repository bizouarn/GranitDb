# Régénère le client typé du client Blazor depuis le contrat OpenAPI de l'API.
#   1. écrit client/api/openapi.json à partir de l'API (le test de contrat le produit, sans démarrer de serveur à la main) ;
#   2. génère client/api/Generated/GranitApiClient.cs avec NSwag.
# Prérequis : le SDK .NET et l'outil NSwag (dotnet tool install -g NSwag.ConsoleCore).
# À relancer chaque fois qu'une route, un paramètre ou un type d'entrée/de réponse de l'API change ; sinon le test
# OpenApiContractTests échoue (le fichier enregistré ne correspond plus à l'API).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $env:GRANITDB_UPDATE_OPENAPI = '1'
    dotnet test tests/GranitDb.Tests --filter "FullyQualifiedName~OpenApiContractTests.The_committed_openapi_file_matches_the_api"
    if ($LASTEXITCODE -ne 0) { throw "Écriture de openapi.json impossible." }
    Remove-Item Env:GRANITDB_UPDATE_OPENAPI

    nswag openapi2csclient `
        /input:client/api/openapi.json `
        /output:client/api/Generated/GranitApiClient.cs `
        /generateContractsOutput:true `
        /contractsOutput:client/api/Generated/GranitApiContracts.cs `
        /contractsNamespace:GranitDb.Client.Models `
        /additionalNamespaceUsages:GranitDb.Client.Models `
        /namespace:GranitDb.Client.Generated `
        /arrayType:System.Collections.Generic.List `
        /arrayInstanceType:System.Collections.Generic.List `
        /responseArrayType:System.Collections.Generic.List `
        '/className:{controller}Client' `
        /operationGenerationMode:MultipleClientsFromOperationId `
        /generateClientInterfaces:true `
        /generateOptionalParameters:true `
        /jsonLibrary:SystemTextJson `
        /useBaseUrl:false `
        /injectHttpClient:true `
        /disposeHttpClient:false `
        /generateExceptionClasses:true `
        /exceptionClass:GranitApiException `
        /generateDtoTypes:true `
        /dateTimeType:System.DateTimeOffset `
        /generateNullableReferenceTypes:true `
        /wrapResponses:true `
        /wrapResponseMethods:DatabaseInfoClient.GetAll,AuditClient.Get `
        /generateSyncMethods:false
    if ($LASTEXITCODE -ne 0) { throw "Génération NSwag impossible." }
}
finally {
    Remove-Item Env:GRANITDB_UPDATE_OPENAPI -ErrorAction SilentlyContinue
    Pop-Location
}
