using System.Data;

namespace WNTable.Core;

/// <summary>Turns edits made in the data grid into INSERT / UPDATE / DELETE statements keyed by primary key.</summary>
public static class ChangeBuilder
{
    public static bool HasChanges(DataTable data) => CountChanges(data) > 0;

    public static int CountChanges(DataTable data)
    {
        var n = 0;
        foreach (DataRow row in data.Rows)
        {
            if (row.RowState == DataRowState.Modified && ChangedColumns(row).Count == 0) continue;
            if (row.RowState is DataRowState.Added or DataRowState.Modified or DataRowState.Deleted) n++;
        }
        return n;
    }

    public static List<SqlStatement> Build(DbDriver driver, TableRef table, DataTable data, IReadOnlyList<ColumnInfo> columns)
    {
        var byName = columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var pk = data.Columns.Cast<DataColumn>()
            .Where(dc => byName.TryGetValue(dc.Caption, out var ci) && ci.IsPrimaryKey)
            .ToList();
        if (pk.Count == 0 || pk.Count != columns.Count(c => c.IsPrimaryKey))
        {
            throw new InvalidOperationException("Bảng không có khoá chính (primary key) nên không thể sửa trực tiếp.");
        }

        var target = driver.Qualify(table);
        var result = new List<SqlStatement>();

        foreach (DataRow row in data.Rows)
        {
            switch (row.RowState)
            {
                case DataRowState.Deleted:
                {
                    var b = new StatementBuilder(driver, byName);
                    var where = b.Where(row, pk);
                    result.Add(b.Finish($"DELETE FROM {target} WHERE {where.Sql}", $"DELETE FROM {target} WHERE {where.Preview}"));
                    break;
                }
                case DataRowState.Added:
                {
                    var cols = data.Columns.Cast<DataColumn>().Where(dc => row[dc] is not DBNull).ToList();
                    if (cols.Count == 0)
                    {
                        result.Add(new SqlStatement(InsertDefaults(driver, target), [], InsertDefaults(driver, target)));
                        break;
                    }
                    var b = new StatementBuilder(driver, byName);
                    var names = string.Join(", ", cols.Select(dc => driver.Quote(dc.Caption)));
                    var values = cols.Select(dc => b.Value(dc.Caption, row[dc])).ToList();
                    result.Add(b.Finish(
                        $"INSERT INTO {target} ({names}) VALUES ({string.Join(", ", values.Select(v => v.Sql))})",
                        $"INSERT INTO {target} ({names}) VALUES ({string.Join(", ", values.Select(v => v.Preview))})"));
                    break;
                }
                case DataRowState.Modified:
                {
                    var changed = ChangedColumns(row);
                    if (changed.Count == 0) break;
                    var b = new StatementBuilder(driver, byName);
                    var sets = changed.Select(dc => (Col: driver.Quote(dc.Caption), V: b.Value(dc.Caption, row[dc]))).ToList();
                    var where = b.Where(row, pk);
                    result.Add(b.Finish(
                        $"UPDATE {target} SET {string.Join(", ", sets.Select(s => $"{s.Col} = {s.V.Sql}"))} WHERE {where.Sql}",
                        $"UPDATE {target} SET {string.Join(", ", sets.Select(s => $"{s.Col} = {s.V.Preview}"))} WHERE {where.Preview}"));
                    break;
                }
            }
        }
        return result;
    }

    private static string InsertDefaults(DbDriver driver, string target) =>
        driver.Kind == DbKind.MySql ? $"INSERT INTO {target} () VALUES ()" : $"INSERT INTO {target} DEFAULT VALUES";

    private static List<DataColumn> ChangedColumns(DataRow row)
    {
        var list = new List<DataColumn>();
        foreach (DataColumn dc in row.Table.Columns)
        {
            var before = ValueFormatter.Format(row[dc, DataRowVersion.Original]);
            var after = ValueFormatter.Format(row[dc, DataRowVersion.Current]);
            if (!string.Equals(before, after, StringComparison.Ordinal)) list.Add(dc);
        }
        return list;
    }

    private sealed class StatementBuilder(DbDriver driver, Dictionary<string, ColumnInfo> columns)
    {
        private readonly List<KeyValuePair<string, object>> _params = [];

        public (string Sql, string Preview) Value(string column, object value)
        {
            if (value is DBNull) return ("NULL", "NULL");
            var name = "p" + _params.Count;
            _params.Add(new(name, value));
            columns.TryGetValue(column, out var info);
            return (driver.Placeholder(name, info), ValueFormatter.SqlLiteral(value));
        }

        public (string Sql, string Preview) Where(DataRow row, List<DataColumn> pk)
        {
            var parts = pk.Select(dc =>
            {
                var v = Value(dc.Caption, row[dc, DataRowVersion.Original]);
                return (Sql: $"{driver.Quote(dc.Caption)} = {v.Sql}", Preview: $"{driver.Quote(dc.Caption)} = {v.Preview}");
            }).ToList();
            return (string.Join(" AND ", parts.Select(p => p.Sql)), string.Join(" AND ", parts.Select(p => p.Preview)));
        }

        public SqlStatement Finish(string sql, string preview) => new(sql, _params.ToList(), preview + ";");
    }
}
