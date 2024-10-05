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
        Logger.InitLogFile();
        
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
        
        ContentDownloader.Init();

        bool login = HasParameter(args, "-l", "--login");
        
        string username = GetParameter(args, "-u","--username", string.Empty);
        string password = GetParameter(args, "-p","--password", string.Empty);
        ContentDownloader.Config.RememberPassword = HasParameter(args, "-r", "--remember");
        
        uint appId = GetParameter(args, "-a", "--appid", uint.MaxValue);


        ContentDownloader.Config.InstallDirectory = GetParameter(args, "-o", "--output", "");
        ContentDownloader.Config.Verify = HasParameter(args, "", "--verify");
            
        ContentDownloader.Config.Platform = GetParameter(args, "","--os", Utils.GetSteamOS());
        ContentDownloader.Config.Architecture = GetParameter(args, "","--arch", Utils.GetSteamArch());

        if (HasParameter(args, "", "--language") && HasParameter(args, "", "--all-languages"))
        {
            Logger.TraceError("A language and all languages can't be selected at the same time");
            Logger.CloseLogFile();
            return 3;
        }

        ContentDownloader.Config.Language = GetParameter(args, "","--language", "english");
        ContentDownloader.Config.AllLanguages = HasParameter(args, "","--all-languages");
        ContentDownloader.Config.Branch = "public";
        
        string ignoreParam = GetParameter(args, "-i","--ignore", "");

        if (string.IsNullOrEmpty(ignoreParam))
            ContentDownloader.Config.IngoreDepots = null;
        else
        {
            string[] splits =  ignoreParam.Split(',');
            ContentDownloader.Config.IngoreDepots = new int[splits.Length];

            for (int i = 0; i < splits.Length; i++)
                ContentDownloader.Config.IngoreDepots[i] = int.Parse(splits[i]);
        }

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
                Logger.CloseLogFile();
                return 0;
            }
            else
            {
                Logger.TraceError("Failed to login");
                ContentDownloader.ShutdownSteam3();
                Logger.CloseLogFile();
                return 1;
            }
                
        }

        if (appId == uint.MaxValue)
        {
            Logger.TraceError("Invalid app id");
            ContentDownloader.ShutdownSteam3();
            Logger.CloseLogFile();
            return 2;
        }

        for (int i = 0; i < 10; i++)
        {
            Logger.TraceInfo("Trying to connect ({0}/10)", i + 1);
            
            if (InitializeSteam(username, password))
                break;

            if (i == 9)
            {
                Logger.TraceError("Failed to connect to Steam");
                Logger.CloseLogFile();
                return 1;
            }
        }

        try
        {
            await ContentDownloader.DownloadAppAsync(appId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.TraceError("Failed to download {0}. {1} \n{2}", appId, ex.Message, ex.StackTrace);
            ContentDownloader.ShutdownSteam3();
            Logger.CloseLogFile();
            return 1;
        }
        
        ContentDownloader.ShutdownSteam3();

        Logger.CloseLogFile();
        
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
        {
            ContentDownloader.Config.RememberPassword = true;
            return ContentDownloader.InitializeSteam3(username, password);
        }
            
        
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
        Logger.Trace("");
        Logger.Trace("Usage: login:");
        Logger.Trace("       steam-dl -l -u <username> [-p <password>]");
        Logger.Trace("");
        Logger.Trace("Usage: downloading a app:");
        Logger.Trace("       steam-dl -a <id> -u username [-p <password>] [other options]");
        Logger.Trace("");
        Logger.Trace("Parameters:");
        Logger.Trace("  -a/--appid <#>           - the AppID to download.");
        Logger.Trace("  --os <os>                - the operating system for which to download the game (windows, macos or linux, default: OS the program is currently running on)");
        Logger.Trace("  --arch <arch>            - the architecture for which to download the game (32 or 64, default: the host's architecture)");
        Logger.Trace("  --language <lang>        - the language for which to download the game (default: english)");
        Logger.Trace("  --all-languages          - downloads all languages of the game");
        Logger.Trace("");
        Logger.Trace("  -u/--username <user>     - the username of the account to login to for restricted content.");
        Logger.Trace("  -p/--password <pass>     - the password of the account to login to for restricted content.");
        Logger.Trace("  -r/--remember            - if set, remember the password for subsequent logins of this user.");
        Logger.Trace("  -l/--login               - if set, you will just be able to test login into steam. No downloads will be made");
        Logger.Trace("");
        Logger.Trace("  -o/--output <installdir> - the directory in which to place downloaded files.");
        Logger.Trace("  --verify                 - Include checksum verification of files already downloaded");
        Logger.Trace("  -i/--ignore <depots>     - Excludes depots from download (123,456,789)");
    }

    static void PrintVersion(bool printExtra = false)
    {
        var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;
        Logger.Trace($"steam-dl v{version}");
    }
}

