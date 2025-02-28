using Dapper.Contrib.Extensions;
using GranitDB.API.Exceptions;
using GranitDb.Interfaces;

namespace GranitDB.API.Models.Descriptions.Base;

public class InfoBase : IInfoBase
{
    [ExplicitKey] public string Id { get; set; }

    public string Description { get; set; }
    public string MetaInfo { get; set; }

    public static async Task<T?> GetAsync<T>(string id) where T : InfoBase
    {
        await using var db = Config.GetDb();
        return await db.GetAsync<T>(id);
    }

    public static async Task<IEnumerable<T>> GetAllAsync<T>() where T : InfoBase
    {
        await using var db = Config.GetDb();
        return await db.GetAllAsync<T>();
    }

    public static async Task<T> CreateAsync<T>(T info) where T : InfoBase
    {
        await using var db = Config.GetDb();
        if (string.IsNullOrWhiteSpace(info.Id))
            info.Id = Guid.NewGuid().ToString();
        await db.InsertAsync(info);
        return info;
    }

    public static async Task<bool> UpdateAsync<T>(string id, T info) where T : InfoBase
    {
        if (id != info.Id)
            throw new IdMisMatchException();

        await using var db = Config.GetDb();
        var existing = await db.GetAsync<T>(id);
        if (existing == null)
            return false;

        return await db.UpdateAsync(info);
    }

    public static async Task<bool> DeleteAsync<T>(string id) where T : InfoBase
    {
        await using var db = Config.GetDb();
        var info = await db.GetAsync<T>(id);
        return await db.DeleteAsync(info);
    }

    public static async Task<bool> DeleteAsync<T>(T info) where T : InfoBase
    {
        if (info == null) return false;
        await using var db = Config.GetDb();
        return await db.DeleteAsync(info);
    }

    public string GetId()
    {
        return Id;
    }
}