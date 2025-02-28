using Dapper;
using Gabi.Base;
using GranitDB.API.Connectors;
using GranitDB.API.Models;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

Logger.CreateLogger("log.txt");

var config = Config.ReadConfig();
using (var db = config.GetConnection())
{
    var version = db.Query<string>("SELECT TOP(1) [versionNumber] FROM [Version]");
    Log.Information($"Start ... version : {version}");
    await SqlServer.SyncAsync("1", db);
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();