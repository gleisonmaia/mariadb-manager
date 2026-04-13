namespace MesasLog.Infrastructure.Naming;

public static class IdentifierNormalizer
{
    public static string NormalizeDatabase(string name) => name.Trim().ToLowerInvariant();
    public static string NormalizeTable(string name) => name.Trim().ToLowerInvariant();
}
