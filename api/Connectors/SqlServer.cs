using System.Data;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Models.Descriptions.Base;
using Microsoft.Data.SqlClient;

namespace GranitDB.API.Connectors;

public class SqlServer
{
    public static async Task SyncAsync(string id, SqlConnection db)
    {
        var dbName = db.Database;
        db.Open();
        var databasesSchem = db.GetSchema("Databases");
        var database = await InfoBase.GetAsync<DatabaseInfo>(id);
        var update = database != null;
        if (!update)
            database = new DatabaseInfo();
        foreach (DataRow row in databasesSchem.Rows)
            if (row["database_name"] as string == dbName)
            {
                database.Id = id;
                database.Name = row["database_name"] as string;
                database.Type = "SqlServer";
            }

        if (update)
            await InfoBase.UpdateAsync(id, database);
        else
            await InfoBase.CreateAsync(database);

        var tables = new Dictionary<string, TableInfo>();
        foreach (var table in await InfoBase.GetAllAsync<TableInfo>())
            if (table.DatabaseId == id)
                tables.TryAdd(table.Name, table);

        var tablesNames = new List<string>();
        var tablesSchem = db.GetSchema("Tables");
        foreach (DataRow row in tablesSchem.Rows)
        {
            var tableName = row["TABLE_NAME"] as string;
            tablesNames.Add(tableName);
            update = tables.TryGetValue(tableName, out var table);
            if (!update)
            {
                table = new TableInfo
                {
                    Name = tableName,
                    DatabaseId = id,
                    MetaInfo = ""
                };
                await InfoBase.CreateAsync(table);
            }
        }

        foreach (var table in tables.Values)
            if (!tablesNames.Contains(table.Name))
                _ = InfoBase.DeleteAsync(table);

        tables.Clear();
        foreach (var table in await InfoBase.GetAllAsync<TableInfo>())
            if (table.DatabaseId == id)
                tables.TryAdd(table.Name, table);
        var columns = new Dictionary<string, ColumnInfo>();
        var columnsIds = new List<string>();
        foreach (var col in await InfoBase.GetAllAsync<ColumnInfo>())
            if (col.DatabaseId == id)
                columns.TryAdd(col.Name, col);
        var columnsSchem = db.GetSchema("Columns");

        foreach (DataRow row in columnsSchem.Rows)
        {
            var tableName = row["TABLE_NAME"] as string;
            var colName = row["COLUMN_NAME"] as string;
            var dataType = row["DATA_TYPE"] as string;
            var isNullable = row["IS_NULLABLE"] == "NO" ? false : true;
            ColumnInfo columnInfo = null;
            update = tables.TryGetValue(tableName, out var table) && columns.TryGetValue(colName, out columnInfo);
            if (!update)
            {
                columnInfo = new ColumnInfo
                {
                    Name = colName,
                    DataType = dataType,
                    IsNullable = isNullable,
                    TableId = table.Id,
                    DatabaseId = id
                };
                await InfoBase.CreateAsync(columnInfo);
            }
            else if (columnInfo != null)
            {
                columnInfo.DataType = dataType;
                columnInfo.IsNullable = true;
                await InfoBase.UpdateAsync(columnInfo.Id, columnInfo);
            }

            if (columnInfo != null)
                columnsIds.Add(columnInfo.Id);
        }

        foreach (var column in columns.Values)
            if (!columnsIds.Contains(column.Id))
                _ = InfoBase.DeleteAsync(column);
    }
}