namespace MesasLog.Wpf;

internal static class SingleInstanceGuard
{
    private static Mutex? _mutex;

    public static bool TryAcquire(out string? message)
    {
        message = null;
        const string name = @"Global\MariaDBManager.SingleInstance";
        try
        {
            _mutex = new Mutex(true, name, out var createdNew);
            if (!createdNew)
            {
                message = "Já existe uma instância do aplicativo em execução.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            message = "Não foi possível verificar instância única: " + ex.Message;
            return true;
        }
    }
}
