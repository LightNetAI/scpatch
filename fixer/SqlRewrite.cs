using System;
using System.Collections.Generic;
using System.Text;

namespace ScreenConnect
{
    /// <summary>
    /// ScreenConnect's report engine emits two constructs that SQLite accepts and
    /// SQL Server rejects:
    ///
    ///  1. GROUP BY built from the select expressions, which for the report's
    ///     computed columns are correlated scalar subqueries  -> Msg 164.
    ///  2. Duration arithmetic written as (MAX(Time) - MIN(Time)) / 10000000,
    ///     i.e. assuming ticks like SQLite's DateTimeFormat=Ticks. On SQL Server
    ///     the columns are datetime2, so the subtraction is invalid -> Msg 8117.
    ///
    /// Both are rewritten here, only when the exact shapes are present:
    /// durations become DATEDIFF_BIG, and the subqueries are hoisted into a
    /// derived table so the outer query groups on plain columns.
    /// </summary>
    internal static class SqlRewrite
    {
        internal static string Apply(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return sql;
            if (sql.IndexOf("10000000", StringComparison.Ordinal) < 0 &&
                sql.IndexOf("GROUP BY", StringComparison.OrdinalIgnoreCase) < 0) return sql;

            string s = RewriteDurations(sql);
            s = RewriteGroupBy(s);
            return s;
        }

        // ---- (A - B) / 10000000  ->  DATEDIFF_BIG(microsecond, B, A) / 1000000 --

        static string RewriteDurations(string sql)
        {
            int guard = 0;
            int from = 0;
            while (guard++ < 64)
            {
                int p = sql.IndexOf("10000000", from, StringComparison.Ordinal);
                if (p < 0) break;
                from = p + 8;

                int i = p - 1;
                while (i >= 0 && char.IsWhiteSpace(sql[i])) i--;
                if (i < 0 || sql[i] != '/') continue;
                int j = i - 1;
                while (j >= 0 && char.IsWhiteSpace(sql[j])) j--;
                if (j < 0 || sql[j] != ')') continue;

                int open = MatchParenBackward(sql, j);
                if (open < 0) continue;

                int innerStart = open + 1;
                int innerEnd = j - 1;
                while (innerStart <= innerEnd)
                {
                    int a = SkipWsForward(sql, innerStart, innerEnd);
                    int b = SkipWsBackward(sql, innerStart, innerEnd);
                    if (a <= b && sql[a] == '(' && MatchParenForward(sql, a) == b) { innerStart = a + 1; innerEnd = b - 1; }
                    else break;
                }

                int minus = FindTopLevelMinus(sql, innerStart, innerEnd);
                if (minus < 0) continue;

                string left = sql.Substring(innerStart, minus - innerStart).Trim();
                string right = sql.Substring(minus + 1, innerEnd - minus).Trim();
                if (!left.StartsWith("(SELECT", StringComparison.OrdinalIgnoreCase)) continue;
                if (!right.StartsWith("(SELECT", StringComparison.OrdinalIgnoreCase)) continue;

                string repl = "DATEDIFF_BIG(microsecond, " + right + ", " + left + ") / 1000000";
                sql = sql.Substring(0, open) + repl + sql.Substring(p + 8);
                from = open + repl.Length;
            }
            return sql;
        }

        // ---- GROUP BY <subquery list>  ->  derived table + plain columns ------

        static string RewriteGroupBy(string sql)
        {
            int fromIdx = FindTopLevel(sql, " FROM ", 0);
            if (fromIdx < 0) return sql;
            int groupIdx = FindTopLevel(sql, " GROUP BY ", fromIdx + 6);
            if (groupIdx < 0) return sql;

            string head = sql.Substring(0, fromIdx);
            if (!head.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) return sql;

            int selectStart = head.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase) + 6;
            string selectList = head.Substring(selectStart).Trim();

            int tailStart = groupIdx + " GROUP BY ".Length;
            int orderIdx = FindTopLevel(sql, " ORDER BY ", tailStart);
            string groupList = (orderIdx < 0 ? sql.Substring(tailStart) : sql.Substring(tailStart, orderIdx - tailStart)).Trim();
            string orderSql = orderIdx < 0 ? null : sql.Substring(orderIdx);

            if (groupList.IndexOf("(SELECT", StringComparison.OrdinalIgnoreCase) < 0) return sql;

            string fromWhere = sql.Substring(fromIdx, groupIdx - fromIdx).Trim();

            List<string> items = SplitTopLevel(selectList, ',');
            if (items.Count < 2) return sql;

            // optional "TOP n" directly after SELECT
            string top = "";
            if (items[0].TrimStart().StartsWith("TOP ", StringComparison.OrdinalIgnoreCase))
            {
                int sp = items[0].IndexOf(' ');
                int sp2 = items[0].IndexOf(' ', sp + 1);
                if (sp2 > 0)
                {
                    top = items[0].Substring(0, sp2).Trim() + " ";
                    items[0] = items[0].Substring(sp2).Trim();
                }
            }

            var inner = new List<string>();
            var outer = new List<string>();
            var exprToAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int k = 0; k < items.Count; k++)
            {
                string item = items[k].Trim();
                if (item.Length == 0) return sql;

                string expr = item, alias = null;
                int asIdx = LastTopLevelAs(item);
                if (asIdx > 0)
                {
                    expr = item.Substring(0, asIdx).Trim();
                    alias = item.Substring(asIdx + 4).Trim();
                }

                bool aggregate = expr.StartsWith("SUM(", StringComparison.OrdinalIgnoreCase)
                                 || expr.StartsWith("COUNT(", StringComparison.OrdinalIgnoreCase)
                                 || expr.StartsWith("MIN(", StringComparison.OrdinalIgnoreCase)
                                 || expr.StartsWith("MAX(", StringComparison.OrdinalIgnoreCase)
                                 || expr.StartsWith("AVG(", StringComparison.OrdinalIgnoreCase);

                if (aggregate)
                {
                    int lp = expr.IndexOf('(');
                    int rp = MatchParenForward(expr, lp);
                    if (rp < 0) return sql;
                    string arg = expr.Substring(lp + 1, rp - lp - 1);
                    string innerAlias = "__scagg" + k;
                    inner.Add(StripOuterParens(arg) + " AS " + innerAlias);
                    outer.Add(expr.Substring(0, lp) + "(" + innerAlias + ")" + (alias != null ? " AS " + alias : ""));
                }
                else
                {
                    string innerAlias = alias ?? ("__sccol" + k);
                    inner.Add(expr + " AS " + innerAlias);
                    outer.Add(innerAlias + (alias != null ? " AS " + alias : ""));
                    string exprKey = Normalize(StripOuterParens(expr));
                    if (!exprToAlias.ContainsKey(exprKey)) exprToAlias[exprKey] = innerAlias;
                }
            }

            List<string> groupItems = SplitTopLevel(groupList, ',');
            var newGroup = new List<string>();
            foreach (string g in groupItems)
            {
                string key = Normalize(StripOuterParens(g.Trim()));
                string alias;
                if (exprToAlias.TryGetValue(key, out alias)) newGroup.Add(alias);
                else return sql;   // unknown shape - leave the query untouched
            }

            string innerSql = "SELECT " + string.Join(", ", inner.ToArray()) + " " + fromWhere;
            string outerSql = "SELECT " + top + string.Join(", ", outer.ToArray())
                            + " FROM (" + innerSql + ") AS __sc"
                            + " GROUP BY " + string.Join(", ", newGroup.ToArray())
                            + (orderSql != null ? " " + orderSql.Trim() : "");

            return outerSql;
        }

        // ---- helpers ---------------------------------------------------------

        static string Normalize(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char c in s.Trim())
            {
                if (char.IsWhiteSpace(c)) { space = true; continue; }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        static string StripOuterParens(string s)
        {
            while (true)
            {
                s = s.Trim();
                if (s.Length < 2 || s[0] != '(') return s;
                if (MatchParenForward(s, 0) != s.Length - 1) return s;
                s = s.Substring(1, s.Length - 2);
            }
        }

        static int LastTopLevelAs(string item)
        {
            int depth = 0;
            for (int i = item.Length - 4; i >= 0; i--)
            {
                char c = item[i];
                if (c == ')') depth++;
                else if (c == '(') depth--;
                else if (depth == 0 && i + 3 < item.Length
                         && (c == 'A' || c == 'a')
                         && (item[i + 1] == 'S' || item[i + 1] == 's')
                         && char.IsWhiteSpace(item[i + 2])
                         && i > 0 && char.IsWhiteSpace(item[i - 1]))
                    return i - 1;
            }
            return -1;
        }

        static List<string> SplitTopLevel(string s, char sep)
        {
            var list = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == '\'' )
                {
                    i++;
                    while (i < s.Length)
                    {
                        if (s[i] == '\'') { if (i + 1 < s.Length && s[i + 1] == '\'') { i += 2; continue; } break; }
                        i++;
                    }
                }
                else if (c == sep && depth == 0)
                {
                    list.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            list.Add(s.Substring(start));
            return list;
        }

        static int FindTopLevel(string s, string token, int from)
        {
            int depth = 0;
            for (int i = from; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (depth == 0 && c == ' ' && StartsWithIgnoreCase(s, i, token)) return i;
            }
            return -1;
        }

        static bool StartsWithIgnoreCase(string s, int idx, string token)
        {
            if (idx + token.Length > s.Length) return false;
            return string.Compare(s, idx, token, 0, token.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }

        static int MatchParenForward(string s, int open)
        {
            if (open < 0 || open >= s.Length || s[open] != '(') return -1;
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        static int MatchParenBackward(string s, int close)
        {
            int depth = 0;
            for (int i = close; i >= 0; i--)
            {
                if (s[i] == ')') depth++;
                else if (s[i] == '(') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        static int SkipWsForward(string s, int start, int end)
        {
            while (start <= end && char.IsWhiteSpace(s[start])) start++;
            return start;
        }

        static int SkipWsBackward(string s, int start, int end)
        {
            while (end >= start && char.IsWhiteSpace(s[end])) end--;
            return end;
        }

        static int FindTopLevelMinus(string s, int start, int end)
        {
            int depth = 0;
            for (int i = start; i <= end; i++)
            {
                char c = s[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (depth == 0 && c == '-') return i;
            }
            return -1;
        }
    }
}
