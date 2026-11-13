using System.Reflection;
using System.Text.Json;
using BenchBook.Core.Model;
using Microsoft.Data.Sqlite;

namespace BenchBook.Core.Store;

/// <summary>
/// Reads and writes one record type to its table. Columns are named after the
/// record's properties in camelCase, as in library.json. Dates are stored as
/// ISO 8601 text and point lists as JSON arrays.
/// </summary>
internal sealed class TableMap<T> where T : LibraryRecord
{
    private readonly PropertyInfo[] _properties;
    private readonly string[] _columns;

    public TableMap(string table)
    {
        Table = table;
        _properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .ToArray();
        _columns = _properties.Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).ToArray();
    }

    public string Table { get; }

    public List<T> ReadAll(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {string.Join(", ", _columns)} FROM {Table} ORDER BY rowid";
        return ReadRows(command);
    }

    public void InsertAll(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<T> records)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"INSERT INTO {Table} ({string.Join(", ", _columns)}) VALUES ({string.Join(", ", _columns.Select(c => "$" + c))})";
        var parameters = _columns.Select(c => command.Parameters.Add("$" + c, SqliteType.Text)).ToArray();
        command.Prepare();
        foreach (var record in records)
        {
            for (var i = 0; i < _properties.Length; i++)
                parameters[i].Value = ToColumn(_properties[i].GetValue(record));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Rows matching a WHERE clause, e.g. "projectId = $id".</summary>
    public List<T> ReadWhere(SqliteConnection connection, string where, params (string Name, object Value)[] values) =>
        ReadWhere(connection, where, null, values);

    public List<T> ReadWhere(SqliteConnection connection, string where, SqliteTransaction? transaction, params (string Name, object Value)[] values)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {string.Join(", ", _columns)} FROM {Table} WHERE {where} ORDER BY rowid";
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        return ReadRows(command);
    }

    private List<T> ReadRows(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            // Required members are filled below; this skips the compiler's check.
            var record = (T)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T));
            if (record is Markup markup) { markup.Points = []; markup.Text = ""; }
            for (var i = 0; i < _properties.Length; i++)
                _properties[i].SetValue(record, FromColumn(reader, i, _properties[i].PropertyType));
            rows.Add(record);
        }
        return rows;
    }

    /// <summary>Writes every column of the record over the row with its id. False if there's no such row.</summary>
    public bool Update(SqliteConnection connection, T record, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var columns = _columns.Where(c => c != "id").ToArray();
        command.CommandText = $"UPDATE {Table} SET {string.Join(", ", columns.Select(c => $"{c} = ${c}"))} WHERE id = $id";
        for (var i = 0; i < _properties.Length; i++)
            command.Parameters.AddWithValue("$" + _columns[i], ToColumn(_properties[i].GetValue(record)));
        return command.ExecuteNonQuery() == 1;
    }

    private static object ToColumn(object? value) => value switch
    {
        null => DBNull.Value,
        DateTimeOffset date => IsoDate.Write(date),
        double[] points => JsonSerializer.Serialize(points),
        _ => value,
    };

    private static object? FromColumn(SqliteDataReader reader, int index, Type type)
    {
        if (reader.IsDBNull(index)) return null;
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target == typeof(string)) return reader.GetString(index);
        if (target == typeof(DateTimeOffset)) return IsoDate.Read(reader.GetString(index));
        if (target == typeof(double)) return reader.GetDouble(index);
        if (target == typeof(int)) return reader.GetInt32(index);
        if (target == typeof(long)) return reader.GetInt64(index);
        if (target == typeof(double[])) return JsonSerializer.Deserialize<double[]>(reader.GetString(index)) ?? [];
        throw new NotSupportedException($"No column mapping for {type}.");
    }
}
