namespace steam_dl;

public static class Logger
{
    public static void TraceError(string msg, params object[] args)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(msg, args);
        Console.ForegroundColor = ConsoleColor.White;
    }
    
    public static void Trace(string msg, params object[] args)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(msg, args);
        Console.ForegroundColor = ConsoleColor.White;
    }
    
    public static void TraceInfo(string msg, params object[] args)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(msg, args);
        Console.ForegroundColor = ConsoleColor.White;
    }
    
    public static void TraceWarning(string msg, params object[] args)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(msg, args);
        Console.ForegroundColor = ConsoleColor.White;
    }
    
    public static void TraceDebug(string msg, params object[] args)
    {
#if DEBUG
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(msg, args);
        Console.ForegroundColor = ConsoleColor.White;
#endif
    }
}