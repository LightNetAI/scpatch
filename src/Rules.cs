using System.Linq;
using System;
using System.Collections.Generic;
using System.Text;

// The rewrite rules.  They are expressed purely in terms of the decrypted
// plaintext and of the method that owns the string, so they keep working
// across ScreenConnect versions even if string indices or keys change.
sealed class Rules
{
    // Words that are reserved in T-SQL and that ScreenConnect uses as bare
    // column identifiers.  KEY is the one that breaks the security database.
    readonly HashSet<string> idents = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Key" };

    // Table whose column definitions must be quoted: patching the
    // ColumnDefinition name makes the generated SELECT list, the PRIMARY KEY
    // clause and every identifier derived from it come out bracketed.
    const string SecurityTable = "SecurityConfiguration";

    readonly Dictionary<string, HashSet<string>> methodStrings;

    public Rules(IEnumerable<string> extra, Dictionary<string, HashSet<string>> methodStrings)
    {
        foreach (var e in extra) if (!string.IsNullOrWhiteSpace(e)) idents.Add(e.Trim());
        this.methodStrings = methodStrings;
    }

    public string Transform(SiteModel site)
    {
        string p = site.Plain;
        if (string.IsNullOrEmpty(p)) return null;

        // 1) SQL text: bracket reserved identifiers used as bare words.
        if (LooksLikeSql(p)) return QuoteIdentifiers(p);

        // 2) Column definitions of the security table: a bare identifier that is
        //    exactly a reserved word, inside a method that also names the table.
        if (idents.Contains(p)
            && methodStrings.TryGetValue(site.Method, out var set)
            && set.Any(x => string.Equals(x, SecurityTable, StringComparison.OrdinalIgnoreCase)))
            return "[" + p + "]";

        return null;
    }

    static readonly string[] SqlMarkers =
    {
        "SELECT ", " FROM ", " WHERE ", "INSERT INTO", "UPDATE ", "DELETE FROM",
        "CREATE TABLE", "ALTER TABLE", "ORDER BY", "GROUP BY", "VALUES (", "SET "
    };

    static bool LooksLikeSql(string s)
    {
        foreach (var m in SqlMarkers) if (s.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    // Brackets bare-word identifiers, skipping quoted literals, parameters,
    // already-bracketed names and dotted names.
    string QuoteIdentifiers(string sql)
    {
        var sb = new StringBuilder(sql.Length + 32);
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];

            // single-quoted literal -> copy verbatim
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

            // parameters
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
                bool prevDot = i > 0 && sql[i - 1] == '.';
                sb.Append(prevDot || !idents.Contains(word) ? word : "[" + word + "]");
                i = j;
                continue;
            }

            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
