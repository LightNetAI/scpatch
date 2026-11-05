using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

// scpatch — rewrite the ByteHide-encrypted string constants inside a ScreenConnect
// assembly so that SQL identifiers which are reserved in T-SQL get bracketed.
//
//   scpatch report <in.dll> <out.tsv>
//   scpatch patch  <in.dll> <out.dll> [--key KEY] [--verbose]
//
// No offsets or addresses are used: every call site is found through the
// obfuscator's own signature, decrypted with the per-site key (sum of the
// call arguments), optionally rewritten, and re-encrypted with that same key.
// That keeps the patch independent of the ScreenConnect version.

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: scpatch report <in.dll> <out.tsv>");
            Console.Error.WriteLine("       scpatch hook   <in.dll> <out.dll> --fixer <ScreenConnect.SqlFixer.dll>");
            Console.Error.WriteLine("       scpatch patch  <in.dll> <out.dll> [--verbose] [--extra-ident NAME]");
            return 2;
        }
        string mode = args[0], input = args[1], output = args[2];
        bool verbose = args.Contains("--verbose");
        var extra = new List<string>();
        for (int i = 3; i < args.Length - 1; i++)
            if (args[i] == "--extra-ident") extra.Add(args[i + 1]);

        ModuleDefMD mod;
        try { mod = ModuleDefMD.Load(input); }
        catch (Exception ex) { Console.Error.WriteLine("load failed: " + ex.Message); return 3; }

        var table = FindStringTable(mod, out var tableIns);
        var decryptors = FindDecryptors(mod).Select(m => m.MDToken.Raw).ToHashSet();
        var sites = new List<SiteModel>();

        foreach (var type in mod.GetTypes())
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                var ins = method.Body.Instructions;
                for (int i = 0; i < ins.Count; i++)
                {
                    var instr = ins[i];
                    if (instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt) continue;
                    if (!(instr.Operand is IMethod mr) || !decryptors.Contains(mr.MDToken.Raw)) continue;
                    var s = TryReadSite(method, ins, i, table, tableIns);
                    if (s != null) sites.Add(s);
                }
            }

        Console.WriteLine($"[i] string table: {table.Count} entries; decryptor call sites: {sites.Count}");

        if (mode == "il")
        {
            string typeName = args.Length > 2 ? args[2] : "ScreenConnect.DbExtensions";
            string methName = args.Length > 3 ? args[3] : "CreateCommand";
            int count = args.Length > 4 ? int.Parse(args[4]) : 12;
            TypeDef t = null;
            foreach (var tt in mod.GetTypes()) if (tt.FullName == typeName) { t = tt; break; }
            if (t == null) { Console.Error.WriteLine("[!] type not found: " + typeName); return 5; }
            foreach (var m in t.Methods)
            {
                if (m.Name != methName) continue;
                Console.WriteLine("== " + m.FullName);
                if (!m.HasBody) { Console.WriteLine("   (no body)"); continue; }
                int n = 0;
                foreach (var i in m.Body.Instructions)
                {
                    Console.WriteLine("   IL_" + i.Offset.ToString("x4") + ": " + i.OpCode.Name + " " + (i.Operand ?? ""));
                    if (++n >= count) break;
                }
            }
            Console.WriteLine("== assembly refs");
            foreach (var r in mod.GetAssemblyRefs()) Console.WriteLine("   " + r.FullName);
            return 0;
        }

        if (mode == "hook")
        {
            string fixerPath = null;
            for (int i = 3; i < args.Length - 1; i++) if (args[i] == "--fixer") fixerPath = args[i + 1];
            if (fixerPath == null) { Console.Error.WriteLine("[!] --fixer <dll> required"); return 2; }
            return Hook(mod, output, fixerPath);
        }

        if (mode == "report")
        {
            using var w = new StreamWriter(output);
            w.WriteLine("type\tmethod\tkind\tindex\tkey\tplain");
            foreach (var s in sites)
                w.WriteLine($"{s.Type}\t{s.Method}\t{s.Kind}\t{s.Index}\t{Convert.ToString(s.Key16, 16)}\t{Esc(s.Plain)}");
            Console.WriteLine($"[i] wrote {sites.Count} sites to {output}");
            return 0;
        }

        var methodStrings = new Dictionary<string, HashSet<string>>();
        foreach (var s0 in sites)
        {
            if (!methodStrings.TryGetValue(s0.Method, out var set)) methodStrings[s0.Method] = set = new HashSet<string>();
            set.Add(s0.Plain);
        }
        var ruled = new Rules(extra, methodStrings);
        int changed = 0;
        var report = new StringBuilder();
        foreach (var s in sites)
        {
            string replacement = ruled.Transform(s);
            if (replacement == null || replacement == s.Plain) continue;
            if (!s.CanWrite) { Console.WriteLine($"[!] cannot write {s.Method}#{s.Index}"); continue; }
            s.Write(replacement);
            changed++;
            report.AppendLine($"{s.Method}#{s.Index}: {Esc(s.Plain)}  ->  {Esc(replacement)}");
            if (verbose) Console.WriteLine($"[+] {s.Method}#{s.Index}: {Esc(s.Plain)} -> {Esc(replacement)}");
        }

        File.WriteAllText(output + ".changes.txt", report.ToString());
        Console.WriteLine($"[i] patched {changed} string site(s); changes listed in {output}.changes.txt");
        if (changed == 0) { Console.Error.WriteLine("[!] nothing matched — refusing to write an unmodified assembly"); return 4; }

        var opts = new ModuleWriterOptions(mod);
        opts.MetadataOptions.Flags |= MetadataFlags.KeepOldMaxStack;
        mod.Write(output, opts);
        Console.WriteLine("[i] wrote " + output);
        return 0;
    }

    // ---------- site model ----------


    internal static string Xor(string s, int key16)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append((char)(c ^ key16));
        return sb.ToString();
    }

    // ---------- obfuscator support ----------

    static List<string> FindStringTable(ModuleDef mod, out List<Instruction> tableIns)
    {
        // ByteHide keeps the ciphertext in Lazy<string[]>[] chunks (1000 each).
        // Follow the obfuscator's own initialiser: the chunk lambdas are the
        // ldftn operands, in array order, of the method that assigns the field.
        FieldDef chunkField = null;
        foreach (var t in mod.GetTypes())
            foreach (var f in t.Fields)
            {
                if (chunkField != null) break;
                var fs = f.FieldSig;
                if (fs == null) continue;
                if (!(fs.Type is SZArraySig outer)) continue;
                if (!(outer.Next is GenericInstSig g)) continue;
                if (g.GenericArguments.Count != 1) continue;
                if (!(g.GenericArguments[0] is SZArraySig inner) || inner.Next.ElementType != ElementType.String) continue;
                chunkField = f;
            }

        var chunks = new List<MethodDef>();
        if (chunkField != null)
        {
            uint tok = chunkField.MDToken.Raw;
            foreach (var t in mod.GetTypes())
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    if (!m.Body.Instructions.Any(i => i.OpCode.Code == Code.Stsfld && i.Operand is IField f2 && f2.MDToken.Raw == tok))
                        continue;
                    foreach (var i in m.Body.Instructions)
                        if (i.OpCode.Code == Code.Ldftn && i.Operand is IMethod fm)
                        {
                            var def = fm.ResolveMethodDef();
                            if (def != null && def.HasBody) chunks.Add(def);
                        }
                    break;
                }
        }

        var all = new List<string>();
        tableIns = new List<Instruction>();
        foreach (var c in chunks)
            foreach (var i in c.Body.Instructions)
                if (i.OpCode.Code == Code.Ldstr)
                {
                    all.Add((i.Operand as UTF8String)?.String ?? (string)i.Operand);
                    tableIns.Add(i);
                }
        if (all.Count > 0) return all;

        // fallback: largest string[]-returning method
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                if (!(m.ReturnType is SZArraySig sa) || sa.Next.ElementType != ElementType.String) continue;
                var lits = m.Body.Instructions.Where(i => i.OpCode.Code == Code.Ldstr).ToList();
                if (lits.Count > all.Count)
                {
                    all = lits.Select(i => (i.Operand as UTF8String)?.String ?? (string)i.Operand).ToList();
                    tableIns = lits;
                }
            }
        return all;
    }

    static IEnumerable<MethodDef> FindDecryptors(ModuleDef mod)
    {
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                var sig = m.MethodSig;
                if (sig == null || sig.Params.Count != 2) continue;
                if (!(sig.Params[0] is ByRefSig br) || br.Next.ElementType != ElementType.String) continue;
                if (!(sig.Params[1] is SZArraySig arr) || arr.Next.ElementType != ElementType.Object) continue;
                yield return m;
            }
    }

    static SiteModel TryReadSite(MethodDef method, IList<Instruction> ins, int callIdx,
                            List<string> table, List<Instruction> tableIns)
    {
        var vals = ReadArgArray(ins, callIdx);
        if (vals == null) return null;
        int key = 0;
        foreach (var v in vals)
        {
            if (v is int iv) key += iv;
            else if (v is string sv) foreach (var c in sv) key += c;
            else if (v is bool bv) key += bv ? 1 : 0;
        }

        // find the local whose address is passed (ldloca) and the store that defines it
        int locIdx = -1; Local local = null;
        for (int k = callIdx - 1; k >= 0 && k >= callIdx - 200; k--)
        {
            var c = ins[k].OpCode.Code;
            if (c == Code.Ldloca || c == Code.Ldloca_S) { locIdx = k; local = (Local)ins[k].Operand; break; }
            if (c == Code.Ret) break;
        }
        if (locIdx < 0) return null;

        int def = -1;
        for (int k = locIdx - 1; k >= 0 && k >= locIdx - 80; k--)
        {
            if (IsStoreTo(ins[k], local)) { def = k; break; }
            if (ins[k].OpCode.Code == Code.Ret) break;
        }
        if (def < 1) return null;

        var site = new SiteModel { Type = method.DeclaringType.FullName, Method = method.FullName, Key = key, Key16 = key & 0xFFFF };
        var val = ins[def - 1];

        if (val.OpCode.Code == Code.Ldstr)
        {
            site.Kind = "inline";
            site.Index = -1;
            site.CipherLiteral = val;
            site.Plain = Xor((val.Operand as UTF8String)?.String ?? "", site.Key16);
            return site;
        }

        // (Cipher)1651 -> op_Implicit(struct) -> op_Implicit(string)
        if (val.OpCode.Code == Code.Call && val.Operand is IMethod conv && conv.Name == "op_Implicit"
            && def >= 2 && ins[def - 2].OpCode.Code == Code.Call)
        {
            var idxIns = ins[def - 3];
            int idx;
            switch (idxIns.OpCode.Code)
            {
                case Code.Ldc_I4: idx = (int)idxIns.Operand; break;
                case Code.Ldc_I4_S: idx = (sbyte)idxIns.Operand; break;
                case Code.Ldc_I4_0: idx = 0; break;
                case Code.Ldc_I4_1: idx = 1; break;
                case Code.Ldc_I4_2: idx = 2; break;
                case Code.Ldc_I4_3: idx = 3; break;
                case Code.Ldc_I4_4: idx = 4; break;
                case Code.Ldc_I4_5: idx = 5; break;
                case Code.Ldc_I4_6: idx = 6; break;
                case Code.Ldc_I4_7: idx = 7; break;
                case Code.Ldc_I4_8: idx = 8; break;
                default: return null;
            }
            if (idx < 0 || idx >= table.Count) return null;
            site.Kind = "table";
            site.Index = idx;
            site.CipherLiteral = tableIns[idx];
            site.Plain = Xor(table[idx], site.Key16);
            return site;
        }
        return null;
    }

    static bool IsStoreTo(Instruction i, Local v)
    {
        switch (i.OpCode.Code)
        {
            case Code.Stloc: case Code.Stloc_S: return (Local)i.Operand == v;
            case Code.Stloc_0: return v.Index == 0;
            case Code.Stloc_1: return v.Index == 1;
            case Code.Stloc_2: return v.Index == 2;
            case Code.Stloc_3: return v.Index == 3;
            default: return false;
        }
    }

    static List<object> ReadArgArray(IList<Instruction> ins, int callIdx)
    {
        for (int i = callIdx - 1; i >= 0 && i >= callIdx - 150; i--)
        {
            if (ins[i].OpCode.Code != Code.Newarr) continue;
            var vals = new List<object>();
            for (int k = i + 1; k < callIdx; k++)
            {
                if (ins[k].OpCode.Code != Code.Stelem_Ref) continue;
                int vi = k - 1;
                while (vi > i && ins[vi].OpCode.Code == Code.Box) vi--;
                var o = Const(ins[vi]);
                if (o == null) return null;
                vals.Add(o);
            }
            return vals.Count > 0 ? vals : null;
        }
        return null;
    }

    static object Const(Instruction v)
    {
        switch (v.OpCode.Code)
        {
            case Code.Ldstr: return (v.Operand as UTF8String)?.String ?? (string)v.Operand;
            case Code.Ldc_I4_M1: return -1;
            case Code.Ldc_I4_0: return 0;
            case Code.Ldc_I4_1: return 1;
            case Code.Ldc_I4_2: return 2;
            case Code.Ldc_I4_3: return 3;
            case Code.Ldc_I4_4: return 4;
            case Code.Ldc_I4_5: return 5;
            case Code.Ldc_I4_6: return 6;
            case Code.Ldc_I4_7: return 7;
            case Code.Ldc_I4_8: return 8;
            case Code.Ldc_I4_S: return (int)(sbyte)v.Operand;
            case Code.Ldc_I4: return (int)v.Operand;
            case Code.Ldnull: return 0;
            default: return null;
        }
    }

    internal static string Esc(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) sb.Append(c >= 0x20 && c < 0x7f ? c.ToString() : "\\u" + ((int)c).ToString("x4"));
        return sb.ToString();
    }

    static int Hook(ModuleDef mod, string output, string fixerPath)
    {
        var helper = ModuleDefMD.Load(fixerPath);
        var fixerType = helper.Find("ScreenConnect.SqlFixer", false);
        if (fixerType == null) { Console.Error.WriteLine("[!] ScreenConnect.SqlFixer not found in helper"); return 3; }
        MethodDef fix = null;
        foreach (var m in fixerType.Methods)
            if (m.Name == "Fix" && m.IsStatic && m.MethodSig.Params.Count == 2) { fix = m; break; }
        if (fix == null) { Console.Error.WriteLine("[!] Fixer.Fix(object,string) not found"); return 3; }

        var targetType = mod.Find("ScreenConnect.DbExtensions", false);
        if (targetType == null) { Console.Error.WriteLine("[!] ScreenConnect.DbExtensions not found"); return 5; }
        MethodDef target = null;
        foreach (var m in targetType.Methods)
        {
            if (m.Name != "CreateCommand" || !m.IsStatic) continue;
            var ps = m.MethodSig.Params;
            if (ps.Count != 3) continue;
            if (!(ps[2] is SZArraySig arr) || arr.Next.ElementType != ElementType.Object) continue;
            target = m; break;
        }
        if (target == null || !target.HasBody) { Console.Error.WriteLine("[!] CreateCommand(DbConnection,string,object[]) not found"); return 5; }

        IMethod imported;
        try { imported = mod.Import(fix); }
        catch (Exception ex) { Console.Error.WriteLine("[!] import failed: " + ex.Message); return 6; }

        var body = target.Body;
        var first = body.Instructions[0];
        if (first.OpCode.Code == Code.Call && first.Operand is IMethod im && im.Name == "Fix")
        {
            Console.WriteLine("[i] already hooked — nothing to do");
            return 0;
        }

        var ins = body.Instructions;
        ins.Insert(0, Instruction.Create(OpCodes.Ldarg_0));
        ins.Insert(1, Instruction.Create(OpCodes.Ldarg_1));
        ins.Insert(2, Instruction.Create(OpCodes.Call, imported));
        ins.Insert(3, Instruction.Create(OpCodes.Starg, target.Parameters[1]));

        Console.WriteLine("[i] hooked " + target.FullName);
        Console.WriteLine("[i]   -> " + imported.FullName);

        var opts = new ModuleWriterOptions(mod);
        opts.MetadataOptions.Flags |= MetadataFlags.KeepOldMaxStack;
        mod.Write(output, opts);
        Console.WriteLine("[i] wrote " + output);
        return 0;
    }

}
