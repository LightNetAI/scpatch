using dnlib.DotNet.Emit;
using dnlib.DotNet;
internal sealed class SiteModel
{
    public string Type, Method, Kind;
    public int Index, Key16;
    public string Plain;
    public Instruction CipherLiteral;   // the ldstr holding the ciphertext
    public int Key;

    public bool CanWrite => CipherLiteral != null;

    public void Write(string newPlain)
    {
        CipherLiteral.Operand = Program.Xor(newPlain, Key16);
    }
}
