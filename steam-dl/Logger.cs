using System.Text;

namespace steam_dl;

public static class Logger
{
    private static FileStream log = null;
    
    public static void InitLogFile()
    {
        if(!Directory.Exists("Logs"))
            Directory.CreateDirectory("Logs");

        log = File.Create(Path.Combine("Logs", DateTime.Now.ToString("yyyyMMddHHmmss") + ".log" ));
    }

    public static void CloseLogFile()
    {
        if (log == null)
            return;
        
        log.Flush();
        log.Close();

        log = null;
    }

    private static void WriteLine(string msg, params object[] args)
    {
        if (log == null)
            return;
        
        log.Write(Encoding.UTF8.GetBytes(string.Format(msg + "\n", args)));
    }
    
    public static void TraceError(string msg, params object[] args)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(msg, args);
        WriteLine("Error: " + msg, args);
        Console.ForegroundColor = ConsoleColor.White;
    }
    
    public static void Trace(string msg, params object[] args)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(msg, args);
        WriteLine(msg, args);
        Console.ForegroundColor = ConsoleColor.White;
    }
    
    public static void TraceInfo(string msg, params object[] args)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(msg, args);
        WriteLine("Info: " + msg, args);
        Console.ForegroundColor = ConsoleColor.White;
    }
    
    public static void TraceWarning(string msg, params object[] args)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(msg, args);
        WriteLine("Warning: " + msg, args);
        Console.ForegroundColor = ConsoleColor.White;
    }
    
    public static void TraceDebug(string msg, params object[] args)
    {
#if DEBUG
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(msg, args);
        WriteLine("Debug: " + msg, args);
        Console.ForegroundColor = ConsoleColor.White;
#endif
    }
}