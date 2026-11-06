// sqlite2mssql - copy a ScreenConnect SQLite database into SQL Server.
//
// ScreenConnect ships a fully managed SQLite provider inside
// ScreenConnect.Server.dll (namespace ScreenConnect.SQLite). This tool loads it
// through reflection, so it needs no NuGet packages and no native sqlite3.dll:
// the only requirement is the ScreenConnect installation it was built for.
//
//   sqlite2mssql.exe --db "C:\...\App_Data\Session.db" ^
//                    --target "Data Source=.\SQLEXPRESS;Initial Catalog=ScreenConnect;Integrated Security=True"
//
// Options:
//   --db <file>          SQLite database to read          (required)
//   --target <connstr>   SQL Server connection string     (required)
//   --server-dll <file>  ScreenConnect.Server.dll providing the SQLite provider
//   --create-database    create the target database if it does not exist
//   --truncate           delete existing rows before copying
//   --batch <n>          SqlBulkCopy batch size (default 2000)
//   --dry-run            show the plan, change nothing
//
// Tables are created when missing, with the same shape ScreenConnect uses on
// SQL Server: nvarchar(max) for free text, but a bounded nvarchar for columns
// that take part in a primary key (SQL Server cannot index nvarchar(max)).

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

static class Program
{
    static string DbPath, Target, ServerDll;
    static bool CreateDatabase, Truncate, DryRun;
    static int BatchSize = 2000;

    static int Main(string[] args)
    {
        if (!Parse(args)) { Usage(); return 2; }

        try
        {
            // --- source: ScreenConnect's own SQLite provider -------------------
            var asm = Assembly.LoadFrom(ServerDll);
            var factoryType = asm.GetType("ScreenConnect.SQLite.SQLiteFactory", false);
            if (factoryType == null) { Error("ScreenConnect.SQLite.SQLiteFactory not found in " + ServerDll); return 3; }
            var factory = (System.Data.Common.DbProviderFactory)factoryType
                .GetField("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);

            if (CreateDatabase) EnsureDatabase();

            using (var src = factory.CreateConnection())
            {
                src.ConnectionString = "Data Source=" + DbPath + ";Version=3;DateTimeKind=Utc;Foreign Keys=true;";
                src.Open();
                Console.WriteLine("[i] source: " + DbPath);

                var tables = ReadTables(src);
                if (tables.Count == 0) { Error("no tables found"); return 4; }

                var foreignKeys = new List<string>();
                foreach (var t in tables) foreignKeys.AddRange(ForeignKeyClauses(src, t.Name));

                using (var dst = new SqlConnection(Target))
                {
                    dst.Open();
                    Console.WriteLine("[i] target: " + dst.DataSource + " / " + dst.Database);

                    foreach (var t in tables)
                    {
                        var cols = ReadColumns(src, t.Name);
                        var ddl = BuildCreateTable(t.Name, cols);
                        bool exists = TableExists(dst, t.Name);

                        if (!exists)
                        {
                            Console.WriteLine("[+] create " + t.Name + (DryRun ? " (dry run)" : ""));
                            if (!DryRun) Exec(dst, ddl);
                        }
                        else Console.WriteLine("[=] exists " + t.Name);

                        if (Truncate && exists)
                        {
                            Console.WriteLine("[~] truncate " + t.Name);
                            if (!DryRun) Exec(dst, "DELETE FROM " + Quote(t.Name));
                        }

                        long n = CopyTable(src, dst, t.Name, cols);
                        Console.WriteLine("[i]   " + n + " row(s) copied");
                    }

                    foreach (var fk in foreignKeys)
                    {
                        if (DryRun) { Console.WriteLine("[+] fk (dry run) " + fk); continue; }
                        try { Exec(dst, fk); } catch (Exception ex) { Console.WriteLine("[!] fk skipped (" + ex.Message.Trim() + ")"); }
                    }

                    Console.WriteLine();
                    Verify(src, dst, tables);
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Error(ex.ToString());
            return 1;
        }
    }

    // ---- source helpers ----------------------------------------------------

    sealed class TableInfo { public string Name; public string Sql; }
    sealed class ColumnInfo { public string Name, DeclaredType; public bool NotNull; public int Pk; }

    static List<TableInfo> ReadTables(System.Data.Common.DbConnection c)
    {
        var list = new List<TableInfo>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(new TableInfo { Name = r.GetString(0), Sql = r.IsDBNull(1) ? "" : r.GetString(1) });
        }
        return list;
    }

    static List<ColumnInfo> ReadColumns(System.Data.Common.DbConnection c, string table)
    {
        var list = new List<ColumnInfo>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(" + Quote(table) + ")";
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    list.Add(new ColumnInfo
                    {
                        Name = r.GetString(1),
                        DeclaredType = r.IsDBNull(2) ? "" : r.GetString(2),
                        NotNull = !r.IsDBNull(3) && Convert.ToInt32(r.GetValue(3)) != 0,
                        Pk = r.IsDBNull(5) ? 0 : Convert.ToInt32(r.GetValue(5))
                    });
        }
        return list;
    }

    static IEnumerable<string> ForeignKeyClauses(System.Data.Common.DbConnection c, string table)
    {
        string sql;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=@n";
            var p = cmd.CreateParameter(); p.ParameterName = "@n"; p.Value = table; cmd.Parameters.Add(p);
            sql = Convert.ToString(cmd.ExecuteScalar());
        }
        if (string.IsNullOrEmpty(sql)) yield break;
        foreach (Match m in Regex.Matches(sql, @"FOREIGN\s+KEY\s*\((?<cols>[^)]+)\)\s*REFERENCES\s+(?<ref>\[?[A-Za-z0-9_]+)\]?\s*\((?<rcols>[^)]+)\)(?<rest>[^,)]*)", RegexOptions.IgnoreCase))
        {
            var cascade = m.Groups["rest"].Value.IndexOf("CASCADE", StringComparison.OrdinalIgnoreCase) >= 0;
            yield return "ALTER TABLE " + Quote(table) + " ADD CONSTRAINT " + Quote("FK_" + table + "_" + m.Groups["ref"].Value)
                + " FOREIGN KEY (" + m.Groups["cols"].Value + ") REFERENCES " + Quote(m.Groups["ref"].Value.Trim('[', ']'))
                + " (" + m.Groups["rcols"].Value + ")" + (cascade ? " ON DELETE CASCADE" : "");
        }
    }

    // ---- target helpers ----------------------------------------------------

    static string MapType(ColumnInfo c, int pkCount)
    {
        var t = (c.DeclaredType ?? "").ToLowerInvariant();
        bool inKey = c.Pk > 0;
        if (t.Contains("uniqueidentifier")) return "uniqueidentifier";
        if (t.Contains("varbinary") || t.Contains("blob")) return "varbinary(max)";
        if (t.Contains("datetime") || t.Contains("date")) return "datetime2";
        if (t.Contains("bit") || t.Contains("bool")) return "bit";
        if (t.Contains("bigint")) return "bigint";
        if (t.Contains("int")) return "int";
        if (t.Contains("real") || t.Contains("float") || t.Contains("double")) return "float";
        if (t.Contains("decimal") || t.Contains("numeric")) return "decimal(18,6)";
        if (t.Contains("nvarchar") || t.Contains("varchar") || t.Contains("char") || t.Contains("text") || t == "")
        {
            if (!inKey) return "nvarchar(max)";
            // SQL Server index keys are limited to 900 bytes; uniqueidentifier is 16.
            return pkCount > 1 ? "nvarchar(200)" : "nvarchar(450)";
        }
        return "nvarchar(max)";
    }

    static string BuildCreateTable(string table, List<ColumnInfo> cols)
    {
        int pkCount = cols.Count(x => x.Pk > 0);
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(Quote(table)).Append(" (");
        var parts = new List<string>();
        foreach (var c in cols)
            parts.Add(Quote(c.Name) + " " + MapType(c, pkCount) + (c.NotNull ? " NOT NULL" : " NULL"));
        var keys = cols.Where(x => x.Pk > 0).OrderBy(x => x.Pk).Select(x => Quote(x.Name)).ToList();
        if (keys.Count > 0) parts.Add("PRIMARY KEY (" + string.Join(", ", keys.ToArray()) + ")");
        sb.Append(string.Join(", ", parts.ToArray())).Append(")");
        return sb.ToString();
    }

    static long CopyTable(System.Data.Common.DbConnection src, SqlConnection dst, string table, List<ColumnInfo> cols)
    {
        int pkCount = cols.Count(x => x.Pk > 0);
        var dt = new DataTable();
        foreach (var c in cols)
        {
            var clr = ClrType(c, pkCount);
            dt.Columns.Add(c.Name, clr);
        }

        using (var cmd = src.CreateCommand())
        {
            // The SQLite provider parses datetime columns with a strict ISO-8601
            // parser and throws on the space-separated form ScreenConnect writes,
            // so read those columns as text and convert them here.
            cmd.CommandText = "SELECT " + string.Join(", ", cols.Select(c =>
            {
                var q = Quote(c.Name);
                return MapType(c, pkCount) == "datetime2" ? "CAST(" + q + " AS TEXT) AS " + q : q;
            }).ToArray()) + " FROM " + Quote(table);
            using (var r = cmd.ExecuteReader())
            {
                var buf = new object[cols.Count];
                while (r.Read())
                {
                    r.GetValues(buf);
                    var row = dt.NewRow();
                    for (int i = 0; i < cols.Count; i++) row[i] = ConvertValue(buf[i], dt.Columns[i].DataType);
                    dt.Rows.Add(row);
                }
            }
        }

        if (DryRun) return dt.Rows.Count;
        if (dt.Rows.Count == 0) return 0;

        using (var bulk = new SqlBulkCopy(dst))
        {
            bulk.DestinationTableName = Quote(table);
            bulk.BatchSize = BatchSize;
            bulk.BulkCopyTimeout = 0;
            foreach (var c in cols) bulk.ColumnMappings.Add(c.Name, c.Name);
            bulk.WriteToServer(dt);
        }
        return dt.Rows.Count;
    }

    static Type ClrType(ColumnInfo c, int pkCount)
    {
        var t = MapType(c, pkCount);
        if (t == "uniqueidentifier") return typeof(Guid);
        if (t == "datetime2") return typeof(DateTime);
        if (t == "bit") return typeof(bool);
        if (t == "int") return typeof(int);
        if (t == "bigint") return typeof(long);
        if (t == "float") return typeof(double);
        if (t == "decimal(18,6)") return typeof(decimal);
        if (t == "varbinary(max)") return typeof(byte[]);
        return typeof(string);
    }

    static object ConvertValue(object v, Type type)
    {
        if (v == null || v is DBNull) return DBNull.Value;
        if (type == typeof(string)) return Convert.ToString(v, CultureInfo.InvariantCulture);
        if (type == typeof(Guid))
        {
            if (v is Guid) return v;
            if (v is byte[] b && b.Length == 16) return new Guid(b);
            return Guid.Parse(Convert.ToString(v, CultureInfo.InvariantCulture));
        }
        if (type == typeof(DateTime))
        {
            if (v is DateTime) return v;
            // ScreenConnect stores datetimes as .NET ticks (DateTimeFormat=Ticks).
            if (v is long || v is int) return new DateTime(Convert.ToInt64(v), DateTimeKind.Utc);
            var s = Convert.ToString(v, CultureInfo.InvariantCulture);
            long ticks;
            if (s.Length <= 19 &&
                long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out ticks))
                return new DateTime(ticks, DateTimeKind.Utc);
            DateTime parsed;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
                return parsed;
            var formats = new[] {
                "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
                "yyyy-MM-dd HH:mm:ss",         "yyyy-MM-ddTHH:mm:ss",
                "yyyy-MM-dd HH:mm:ss.fff",     "yyyy-MM-ddTHH:mm:ss.fff"
            };
            if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
                return parsed;
            throw new FormatException("unparsable datetime: '" + s + "'");
        }
        if (type == typeof(bool)) return Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0;
        if (type == typeof(int)) return Convert.ToInt32(v, CultureInfo.InvariantCulture);
        if (type == typeof(long)) return Convert.ToInt64(v, CultureInfo.InvariantCulture);
        if (type == typeof(double)) return Convert.ToDouble(v, CultureInfo.InvariantCulture);
        if (type == typeof(decimal)) return Convert.ToDecimal(v, CultureInfo.InvariantCulture);
        if (type == typeof(byte[])) return v as byte[] ?? Encoding.UTF8.GetBytes(Convert.ToString(v, CultureInfo.InvariantCulture));
        return v;
    }

    static void Verify(System.Data.Common.DbConnection src, SqlConnection dst, List<TableInfo> tables)
    {
        Console.WriteLine("table                    sqlite    mssql   ok");
        foreach (var t in tables)
        {
            long a = Scalar(src, "SELECT COUNT(*) FROM " + Quote(t.Name));
            long b = Scalar(dst, "SELECT COUNT(*) FROM " + Quote(t.Name));
            Console.WriteLine("{0,-24} {1,7} {2,7}   {3}", t.Name, a, b, a == b ? "yes" : "NO");
        }
    }

    static long Scalar(IDisposable c, string sql)
    {
        if (c is System.Data.Common.DbConnection) { using (var cmd = ((System.Data.Common.DbConnection)c).CreateCommand()) { cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); } }
        using (var cmd = ((SqlConnection)c).CreateCommand()) { cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
    }

    static bool TableExists(SqlConnection c, string table)
    {
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT OBJECT_ID(@n, 'U')";
            cmd.Parameters.AddWithValue("@n", table);
            return cmd.ExecuteScalar() != null && cmd.ExecuteScalar() != DBNull.Value && Convert.ToInt32(cmd.ExecuteScalar()) != 0;
        }
    }

    static void Exec(SqlConnection c, string sql) { using (var cmd = c.CreateCommand()) { cmd.CommandText = sql; cmd.CommandTimeout = 0; cmd.ExecuteNonQuery(); } }

    static void EnsureDatabase()
    {
        var b = new SqlConnectionStringBuilder(Target);
        var name = b.InitialCatalog;
        if (string.IsNullOrEmpty(name)) throw new Exception("target connection string has no Initial Catalog");
        b.InitialCatalog = "master";
        using (var m = new SqlConnection(b.ConnectionString))
        {
            m.Open();
            using (var cmd = m.CreateCommand())
            {
                cmd.CommandText = "SELECT 1 FROM sys.databases WHERE name = @n";
                cmd.Parameters.AddWithValue("@n", name);
                if (cmd.ExecuteScalar() == null)
                {
                    Console.WriteLine("[+] create database " + name);
                    if (!DryRun) Exec(m, "CREATE DATABASE " + Quote(name));
                }
            }
        }
    }

    static string Quote(string id) { return "[" + id.Replace("]", "]]") + "]"; }

    static bool Parse(string[] a)
    {
        ServerDll = @"C:\Program Files (x86)\ScreenConnect\Bin\ScreenConnect.Server.dll";
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--db": DbPath = a[++i]; break;
                case "--target": Target = a[++i]; break;
                case "--server-dll": ServerDll = a[++i]; break;
                case "--create-database": CreateDatabase = true; break;
                case "--truncate": Truncate = true; break;
                case "--dry-run": DryRun = true; break;
                case "--batch": BatchSize = int.Parse(a[++i]); break;
                case "-h": case "--help": return false;
                default: Console.Error.WriteLine("unknown argument: " + a[i]); return false;
            }
        }
        if (DbPath == null || Target == null) return false;
        if (!File.Exists(DbPath)) { Console.Error.WriteLine("database not found: " + DbPath); return false; }
        if (!File.Exists(ServerDll)) { Console.Error.WriteLine("ScreenConnect.Server.dll not found: " + ServerDll); return false; }
        return true;
    }

    static void Usage()
    {
        Console.WriteLine("sqlite2mssql --db <sqlite file> --target <mssql connection string>");
        Console.WriteLine("             [--server-dll <ScreenConnect.Server.dll>] [--create-database]");
        Console.WriteLine("             [--truncate] [--batch 2000] [--dry-run]");
    }

    static void Error(string s) { Console.Error.WriteLine("[!] " + s); }
}
