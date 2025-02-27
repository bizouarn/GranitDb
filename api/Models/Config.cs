using Gabi.Base;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;

namespace GranitDB.API.Models;

public class Config : DatabaseConfig
{
    private static Config _main = new();

    public static Config ReadConfig()
    {
        _main = Load("config.json");
        return _main;
    }

    public static SqlConnection GetDb()
    {
        return _main.GetConnection();
    }

    private static Config Load(string fileName)
    {
        try
        {
            if (File.Exists(fileName))
            {
                var json = File.ReadAllText(fileName);
                return JsonConvert.DeserializeObject<Config>(json);
            }
        }
        catch (SystemException e)
        {
            throw new BaseException($"Le fichier JSON {fileName} et de type Config n'a pas été trouvé !", e);
        }

        return default;
    }
}