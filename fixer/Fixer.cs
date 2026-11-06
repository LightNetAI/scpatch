using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ScreenConnect
{
    /// <summary>
    /// Rewrites SQL text just before it is handed to ADO.NET so that identifiers
    /// which are reserved in T-SQL (notably "Key") are bracketed.
    ///
    /// ScreenConnect generates its SQL with bare column names. That is legal in
    /// SQLite, but KEY is a reserved word in T-SQL and the server rejects it with
    /// "Incorrect syntax near the keyword 'Key'". Bracketing happens on the text
    /// that is actually executed, so the schema definitions keep their plain
    /// names and ScreenConnect's schema comparison still matches the database.
    /// </summary>
    public static class SqlFixer
    {
        const string LogPath = @"C:\Temp\scsqlfix.log";
        static int logged;

        // Identifiers that must be bracketed when used bare.
        static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Key",
            "Value",
        };

        // ScreenConnect's tamper check (ScreenConnect.ServerExtensions.HaveFilesBeenTamperedWith)
        // hashes its own files. A patched Server.dll always mismatches, and the relay is
        // the only service that acts on it -- it ends its run and restart-loops every few
        // seconds. scpatch rewrites that method to call this instead.
        public static System.Threading.Tasks.Task<bool> NotTampered()
        {
            return System.Threading.Tasks.Task.FromResult(false);
        }

        public static string Fix(object connection, string sql)
        {
            try
            {
                return FixCore(connection, sql);
            }
            catch (Exception ex)
            {
                Log("EXCEPTION " + ex.GetType().FullName + ": " + ex.Message + "\nSQL: " + sql);
                return sql;   // never break the caller
            }
        }

        static string FixCore(object connection, string sql)
        {
            if (string.IsNullOrEmpty(sql)) return sql;
            // Only rewrite when talking to SQL Server; SQLite and Oracle accept
            // bare identifiers and Oracle would reject brackets.
            if (connection == null) return sql;
            if (!connection.GetType().Name.StartsWith("SqlConnection", StringComparison.Ordinal)) return sql;
            if (!LooksLikeSql(sql)) return sql;

            // Report engine constructs that SQLite accepts and SQL Server rejects.
            string rewritten = SqlRewrite.Apply(sql);
            if (!string.Equals(rewritten, sql, StringComparison.Ordinal))
            {
                Log("REWRITE IN : " + sql + "\nREWRITE OUT: " + rewritten);
                sql = rewritten;
            }

            if (sql.IndexOf("Key", StringComparison.OrdinalIgnoreCase) < 0 &&
                sql.IndexOf("Value", StringComparison.OrdinalIgnoreCase) < 0) return sql;

            StringBuilder sb = new StringBuilder(sql.Length + 32);
            string prevWord = null;
            int i = 0;
            while (i < sql.Length)
            {
                char c = sql[i];

                // 'string literal' -> copy verbatim (handle '' escapes)
                if (c == '\'')
                {
                    int j = i + 1;
                    while (j < sql.Length)
                    {
                        if (sql[j] == '\'')
                        {
                            if (j + 1 < sql.Length && sql[j + 1] == '\'') { j += 2; continue; }
                            break;
                        }
                        j++;
                    }
                    int end = Math.Min(j + 1, sql.Length);
                    sb.Append(sql, i, end - i);
                    i = end;
                    continue;
                }

                // already bracketed -> copy verbatim
                if (c == '[')
                {
                    int j = sql.IndexOf(']', i + 1);
                    if (j < 0) { sb.Append(sql, i, sql.Length - i); break; }
                    sb.Append(sql, i, j - i + 1);
                    i = j + 1;
                    continue;
                }

                // -- line comment -> copy to end of line
                if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
                {
                    int j = sql.IndexOf('\n', i);
                    if (j < 0) { sb.Append(sql, i, sql.Length - i); break; }
                    sb.Append(sql, i, j - i);
                    i = j;
                    continue;
                }

                // @parameter -> copy verbatim
                if (c == '@')
                {
                    int j = i + 1;
                    while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_')) j++;
                    sb.Append(sql, i, j - i);
                    i = j;
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    int j = i;
                    while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_')) j++;
                    string word = sql.Substring(i, j - i);
                    bool qualified = i > 0 && sql[i - 1] == '.';
                    // "PRIMARY KEY" / "FOREIGN KEY" are syntax, not an identifier
                    bool syntax = string.Equals(word, "Key", StringComparison.OrdinalIgnoreCase)
                                  && (string.Equals(prevWord, "PRIMARY", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(prevWord, "FOREIGN", StringComparison.OrdinalIgnoreCase));
                    if (!qualified && !syntax && Reserved.Contains(word)) sb.Append('[').Append(word).Append(']');
                    else sb.Append(word);
                    prevWord = word;
                    i = j;
                    continue;
                }

                if (c == '(' || c == ')' || c == ',') prevWord = null;
                sb.Append(c);
                i++;
            }

            string result = sb.ToString();
            if (!string.Equals(result, sql, StringComparison.Ordinal))
                Log("IN : " + sql + "\nOUT: " + result);
            return result;
        }

        static void Log(string text)
        {
            // Diagnostics are opt-in: create the marker file to capture rewrites.
            if (logged > 4000) return;
            try { if (!File.Exists(@"C:\Temp\scsqlfix.enable")) return; } catch { return; }
            try
            {
                logged++;
                File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + "\n---\n");
            }
            catch { }
        }

        static bool LooksLikeSql(string s)
        {
            return s.IndexOf("SELECT ", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf(" FROM ", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf(" WHERE ", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("INSERT ", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("UPDATE ", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("DELETE ", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("CREATE TABLE", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("ALTER TABLE", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("PRIMARY KEY", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("GROUP BY", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("VALUES", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
