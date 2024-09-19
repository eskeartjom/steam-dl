using System.ComponentModel;
using System.Data.SqlTypes;
using System.Reflection;
using System.Runtime.InteropServices;
using SteamKit2.Internal;

namespace steam_dl;

class Program
{
    static async Task<int> Main(string[] args)
    {

        if (args.Length == 0)
        {
            PrintVersion();
            PrintUsage();

            return 0;
        }

        AccountSettingsStore.LoadFromFile("account.config");

        bool version = HasParameter(args, "-v", "--version");
        if (version)
        {
            PrintVersion();
            return 0;
        }

        bool login = HasParameter(args, "-l", "--login");
        
        string username = GetParameter(args, "-u","--username", string.Empty);
        string password = GetParameter(args, "-p","--password", string.Empty);
        ContentDownloader.Config.RememberPassword = HasParameter(args, "-r", "--remember");
        
        uint appId = GetParameter(args, "-a", "--appid", uint.MaxValue);


        ContentDownloader.Config.InstallDirectory = GetParameter(args, "-o", "--output", "");
        ContentDownloader.Config.Verify = HasParameter(args, "", "--verify");
            
        ContentDownloader.Config.Platform = GetParameter(args, "","--os", Utils.GetSteamOS());
        ContentDownloader.Config.Architecture = GetParameter(args, "","--arch", Utils.GetSteamArch());
        ContentDownloader.Config.Language = GetParameter(args, "","--language", "english");
        ContentDownloader.Config.Branch = "public";

        Random rand = new Random();
        byte[] b = new byte[4];
        rand.NextBytes(b);
        ContentDownloader.Config.LoginID = BitConverter.ToUInt32(b);
        
        if (login)
        {
            if (InitializeSteam(username, password))
            {
                Logger.TraceInfo("Logged in successfully");
                ContentDownloader.ShutdownSteam3();
                return 0;
            }
            else
            {
                Logger.TraceError("Failed to login");
                ContentDownloader.ShutdownSteam3();
                return 1;
            }
                
        }

        if (appId == uint.MaxValue)
        {
            Logger.TraceError("Invalid app id");
            ContentDownloader.ShutdownSteam3();
            return 1;
        }
        
        InitializeSteam(username, password);

        try
        {
            await ContentDownloader.DownloadAppAsync(appId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            ContentDownloader.ShutdownSteam3();
            return 1;
        }
     
        ContentDownloader.ShutdownSteam3();
        
        return 0;
    }

    static bool HasParameter(string[] args, string slParam, string mlParam)
    {
        if (args.Contains(slParam) || args.Contains(mlParam))
            return true;

        return false;
    }
    
    static T GetParameter<T>(string[] args, string slParam, string mlParam, T defaultValue)
    {
        int slIndex = Array.IndexOf(args, slParam);
        int mlIndex = Array.IndexOf(args, mlParam);
        
        if (slIndex == -1 && mlIndex == -1)
            return defaultValue;

        int index = -1;

        if (slIndex >= 0)
            index = slIndex;
        else if (mlIndex >= 0)
            index = mlIndex;
        
        if(index == -1 || index == (args.Length - 1))
            return defaultValue;

        string strParam = args[index + 1];

        TypeConverter converter = TypeDescriptor.GetConverter(typeof(T));
        if (converter != null)
            return (T)converter.ConvertFromString(strParam);
            
        return defaultValue;
    }

    static bool InitializeSteam(string username, string password)
    {
        if (string.IsNullOrEmpty(username))
        {
            Logger.TraceInfo("Enter account username: ");
            username = Console.ReadLine();
        }

        if (AccountSettingsStore.Instance.LoginTokens.ContainsKey(username))
            return ContentDownloader.InitializeSteam3(username, password);
        
        if (string.IsNullOrEmpty(password))
        {
            Logger.TraceInfo("Enter account password for {0}: ", username);
            password = Utils.ReadPassword();
            Console.WriteLine();
        }

        return ContentDownloader.InitializeSteam3(username, password);
    }

    static void PrintUsage()
    {
        // Do not use tabs to align parameters here because tab size may differ
        Console.WriteLine();
        Console.WriteLine("Usage: login:");
        Console.WriteLine("       steam-dl -l -u <username> [-p <password>]");
        Console.WriteLine();
        Console.WriteLine("Usage: downloading a app:");
        Console.WriteLine("       depotdownloader -a <id> -u username [-p <password>] [other options]");
        Console.WriteLine();
        Console.WriteLine("Parameters:");
        Console.WriteLine("  -a/--appid <#>           - the AppID to download.");
        Console.WriteLine("  --os <os>                - the operating system for which to download the game (windows, macos or linux, default: OS the program is currently running on)");
        Console.WriteLine("  --arch <arch>            - the architecture for which to download the game (32 or 64, default: the host's architecture)");
        Console.WriteLine("  --language <lang>        - the language for which to download the game (default: english)");
        Console.WriteLine();
        Console.WriteLine("  -u/--username <user>     - the username of the account to login to for restricted content.");
        Console.WriteLine("  -p/--password <pass>     - the password of the account to login to for restricted content.");
        Console.WriteLine("  -r/--remember            - if set, remember the password for subsequent logins of this user.");
        Console.WriteLine("  -l/--login               - if set, you will just be able to test login into steam. No downloads will be made");
        Console.WriteLine();
        Console.WriteLine("  -o/--output <installdir> - the directory in which to place downloaded files.");
        Console.WriteLine("  --verify                 - Include checksum verification of files already downloaded");
    }

    static void PrintVersion(bool printExtra = false)
    {
        var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;
        Console.WriteLine($"steam-dl v{version}");
    }
}

