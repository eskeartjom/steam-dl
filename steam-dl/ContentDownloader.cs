using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using SteamKit2;
using SteamKit2.CDN;
using SteamKit2.Internal;

namespace steam_dl;

public static class ContentDownloader
{
    public const uint INVALID_APP_ID = uint.MaxValue;
    public const uint INVALID_DEPOT_ID = uint.MaxValue;
    public const ulong INVALID_MANIFEST_ID = ulong.MaxValue;
    public const string DEFAULT_BRANCH = "public";
    
    private static List<DepotDownloadInfo> infos = new List<DepotDownloadInfo>();
    public static DownloadConfig Config = new();
    private static Steam3Session steam3;
    private static CDNClientPool cdnPool;
    private const string CONFIG_DIR = ".steam-dl";
    private static readonly string STAGING_DIR = Path.Combine(CONFIG_DIR, "staging");
    private static string DEFAULT_DOWNLOAD_DIR = @"steamapps\common";
    
    private sealed class DepotDownloadInfo(
        uint depotid, uint appId, ulong manifestId, string branch,
        string installDir, byte[] depotKey)
    {
        public uint DepotId { get; } = depotid;
        public uint AppId { get; } = appId;
        public ulong ManifestId { get; } = manifestId;
        public string Branch { get; } = branch;
        public string InstallDir { get; } = installDir;
        public byte[] DepotKey { get; } = depotKey;
    }

    public static void Init()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            DEFAULT_DOWNLOAD_DIR = @"steamapps\common";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            DEFAULT_DOWNLOAD_DIR = @"steamapps/common";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            DEFAULT_DOWNLOAD_DIR = @"steamapps/common";
        
    }

    public static bool InitializeSteam3(string username, string password)
    {
        string loginToken = null;

        if (username != null && AccountSettingsStore.Instance.LoginTokens.ContainsKey(username))// && Config.RememberPassword)
        {
            _ = AccountSettingsStore.Instance.LoginTokens.TryGetValue(username, out loginToken);
        }

        steam3 = new Steam3Session(
            new SteamUser.LogOnDetails
            {
                Username = username,
                Password = loginToken == null ? password : null,
                ShouldRememberPassword = Config.RememberPassword,
                AccessToken = loginToken,
                LoginID = Config.LoginID ?? 0x534B32 // "SK2"
            }
        );

        if (!steam3.WaitForCredentials())
        {
            Logger.TraceError("Unable to get steam3 credentials");
            return false;
        }

        Logger.TraceInfo("Got steam3 credentials");
        return true;
    }
    
    public static void ShutdownSteam3()
    {
        if (cdnPool != null)
        {
            cdnPool.Shutdown();
            cdnPool = null;
        }

        if (steam3 == null)
            return;

        steam3.Disconnect();
    }
    
    public static async Task DownloadAppAsync(uint appId)
    {
        cdnPool = new CDNClientPool(steam3, appId);

        // Load our configuration data containing the depots currently installed
        string configPath = Config.InstallDirectory;

        Directory.CreateDirectory(Path.Combine(configPath, CONFIG_DIR));
        DepotConfigStore.LoadFromFile(Path.Combine(configPath, CONFIG_DIR, "depot.config"));

        steam3?.RequestAppInfo(appId);

        if (!AccountHasAccess(appId))
        {
            if (steam3.RequestFreeAppLicense(appId))
            {
                Logger.TraceInfo("Obtained FreeOnDemand license for app {0}", appId);

                // Fetch app info again in case we didn't get it fully without a license.
                steam3.RequestAppInfo(appId, true);
            }
            else
            {
                var contentName = GetAppName(appId);
                throw new Exception(string.Format("App {0} ({1}) is not available from this account.", appId, contentName));
            }
        }
        
        Logger.TraceInfo("Downloading '{0}'", GetAppName(appId));
        var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);

        List<(uint depotId, ulong manifestId)> depotManifestIds = new List<(uint depotId, ulong manifestId)>();
        
        Logger.TraceInfo("Using app branch: '{0}'", Config.Branch);

        if (depots != null)
        {
            foreach (var depotSection in depots.Children)
            {
                var id = INVALID_DEPOT_ID;
                if (depotSection.Children.Count == 0)
                    continue;

                if (!uint.TryParse(depotSection.Name, out id))
                    continue;
                
                var depotConfig = depotSection["config"];
                if (depotConfig != KeyValue.Invalid)
                {
                    if (depotConfig["oslist"] != KeyValue.Invalid &&
                        !string.IsNullOrWhiteSpace(depotConfig["oslist"].Value))
                    {
                        var oslist = depotConfig["oslist"].Value.Split(',');
                        if (Array.IndexOf(oslist, Config.Platform ?? Utils.GetSteamOS()) == -1)
                            continue;
                    }

                    if (depotConfig["osarch"] != KeyValue.Invalid &&
                        !string.IsNullOrWhiteSpace(depotConfig["osarch"].Value))
                    {
                        var depotArch = depotConfig["osarch"].Value;
                        if (depotArch != (Config.Architecture ?? Utils.GetSteamArch()))
                            continue;
                    }

                    if (!Config.AllLanguages && depotConfig["language"] != KeyValue.Invalid &&
                        !string.IsNullOrWhiteSpace(depotConfig["language"].Value))
                    {
                        var depotLang = depotConfig["language"].Value;
                        if (depotLang != (Config.Language ?? "english"))
                            continue;
                    }
                        
                    depotManifestIds.Add((id, INVALID_MANIFEST_ID));
                }
                else
                {
                    depotManifestIds.Add((id, INVALID_MANIFEST_ID));
                }
            }
        }

        infos = new List<DepotDownloadInfo>();
        
        foreach (var (depotId, manifestId) in depotManifestIds)
        {
            var info = GetDepotInfo(depotId, appId, manifestId, Config.Branch);
            if (info != null)
            {
                infos.Add(info);
            }
        }
        
        try
        {
            await DownloadSteam3Async(infos).ConfigureAwait(false);

            CreateAppManifest(appId);
        }
        catch (OperationCanceledException)
        {
            Logger.TraceError("App {0} was not completely downloaded.", appId);
            throw;
        }
    }
    
    static DepotDownloadInfo GetDepotInfo(uint depotId, uint appId, ulong manifestId, string branch)
    {
        if (steam3 != null && appId != INVALID_APP_ID)
            steam3.RequestAppInfo(appId);

        if (!AccountHasAccess(depotId))
        {
            Logger.TraceError("Depot {0} is not available from this account.", depotId);

            return null;
        }

        if (manifestId == INVALID_MANIFEST_ID)
        {
            manifestId = GetSteam3DepotManifest(depotId, appId, branch);
            if (manifestId == INVALID_MANIFEST_ID && !string.Equals(branch, DEFAULT_BRANCH, StringComparison.OrdinalIgnoreCase))
            {
                Logger.TraceWarning("Depot {0} does not have branch named \"{1}\". Trying {2} branch.", depotId, branch, DEFAULT_BRANCH);
                branch = DEFAULT_BRANCH;
                manifestId = GetSteam3DepotManifest(depotId, appId, branch);
            }

            if (manifestId == INVALID_MANIFEST_ID)
            {
                Logger.TraceError("Depot {0} missing public subsection or manifest section.", depotId);
                return null;
            }
        }

        steam3.RequestDepotKey(depotId, appId);
        if (!steam3.DepotKeys.TryGetValue(depotId, out var depotKey))
        {
            Logger.TraceError("No valid depot key for {0}, unable to download.", depotId);
            return null;
        }

        KeyValue appInfo = GetAllInfo(appId);
        
        var uVersion = GetSteam3AppBuildNumber(appId, branch);
        string steamInstallDir = GetInstallDir(appId);
        
        if (!CreateDirectories(depotId, uVersion, steamInstallDir, out var installDir))
        {
            Logger.TraceError("Error: Unable to create install directories!");
            return null;
        }

        return new DepotDownloadInfo(depotId, appId, manifestId, branch, installDir, depotKey);
    }

    static void CreateAppManifest(uint appId)
    {
        string file = Path.Combine(Config.InstallDirectory, "steamapps", string.Format("appmanifest_{0}.acf", appId));
        
        if(File.Exists(file))
            File.Delete(file);

        FileStream fs = File.Create(file);
        string name = GetAppName(appId);
        string installDir = GetInstallDir(appId);
        uint buildid = GetSteam3AppBuildNumber(appId, Config.Branch);

        KeyValue depots = GetDepotInfos(appId);

        List<(string, string, string)> installedDepots = new List<(string, string, string)>();
        
        foreach (DepotDownloadInfo info in infos)
        {
            KeyValue depot = depots.Children.Where(x => x.Name == info.DepotId.ToString()).FirstOrDefault();
            if (depot == null)
                continue;

            KeyValue manifests = depot.Children.Where(x => x.Name == "manifests").FirstOrDefault();
            if (manifests == null)
                continue;
            
            KeyValue branch = manifests.Children.Where(x => x.Name == Config.Branch).FirstOrDefault();
            if (branch == null)
                continue;
            
            KeyValue gid = branch.Children.Where(x => x.Name == "gid").FirstOrDefault();
            if (gid == null)
                continue;
            
            KeyValue size = branch.Children.Where(x => x.Name == "size").FirstOrDefault();
            if (size == null)
                continue;
            
            installedDepots.Add((info.DepotId.ToString(), gid.Value, size.Value));
        }
        
        string data = "";

        data += $"\"AppState\"\n";
        data += $"{{\n";
        data += $"\t\"appid\"\t\t\"{appId}\"\n";
        data += $"\t\"name\"\t\t\"{name}\"\n";
        data += $"\t\"StateFlags\"\t\t\"4\"\n";
        data += $"\t\"installdir\"\t\t\"{installDir}\"\n";
        data += $"\t\"buildid\"\t\t\"{buildid}\"\n";

        if (installedDepots.Count > 0)
        {
            data += $"\t\"InstalledDepots\"\n";
            data += $"\t{{\n";

            foreach ((string, string, string) depot in installedDepots)
            {
                data += $"\t\t\"{depot.Item1}\"\n";
                data += $"\t\t{{\n";
                data += $"\t\t\t\"manifest\"\t\t\"{depot.Item2}\"\n";
                data += $"\t\t\t\"size\"\t\t\"{depot.Item3}\"\n";
                data += $"\t\t}}\n";
            }
            
            data += $"\t}}\n";
        }
        data += $"}}\n";
        
        fs.Write(Encoding.UTF8.GetBytes(data));
        fs.Flush();
        fs.Close();
    }
    
    static bool CreateDirectories(uint depotId, uint depotVersion, string steamInstalldir, out string installDir)
    {
        installDir = null;
        try
        {
            if (string.IsNullOrWhiteSpace(Config.InstallDirectory))
            {
                Directory.CreateDirectory(DEFAULT_DOWNLOAD_DIR);

                //var depotPath = Path.Combine(DEFAULT_DOWNLOAD_DIR, depotId.ToString());
                //Directory.CreateDirectory(depotPath);

                //installDir = Path.Combine(depotPath, depotVersion.ToString());
                installDir = Path.Combine(DEFAULT_DOWNLOAD_DIR, steamInstalldir);
                Directory.CreateDirectory(installDir);

                Directory.CreateDirectory(Path.Combine(installDir, CONFIG_DIR));
                Directory.CreateDirectory(Path.Combine(installDir, STAGING_DIR));
            }
            else
            {
                Directory.CreateDirectory(Config.InstallDirectory);
                Directory.CreateDirectory(Path.Combine(Config.InstallDirectory, DEFAULT_DOWNLOAD_DIR));
                installDir = Path.Combine(Config.InstallDirectory, DEFAULT_DOWNLOAD_DIR, steamInstalldir);
                
                Directory.CreateDirectory(installDir);

                Directory.CreateDirectory(Path.Combine(installDir, CONFIG_DIR));
                Directory.CreateDirectory(Path.Combine(installDir, STAGING_DIR));
            }
        }
        catch (Exception ex)
        {
            Logger.TraceError("Unable to create directory. {0}", ex.Message);
            return false;
        }

        return true;
    }
    
    static ulong GetSteam3DepotManifest(uint depotId, uint appId, string branch)
    {
        var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
        var depotChild = depots[depotId.ToString()];

        if (depotChild == KeyValue.Invalid)
            return INVALID_MANIFEST_ID;

        // Shared depots can either provide manifests, or leave you relying on their parent app.
        // It seems that with the latter, "sharedinstall" will exist (and equals 2 in the one existance I know of).
        // Rather than relay on the unknown sharedinstall key, just look for manifests. Test cases: 111710, 346680.
        if (depotChild["manifests"] == KeyValue.Invalid && depotChild["depotfromapp"] != KeyValue.Invalid)
        {
            var otherAppId = depotChild["depotfromapp"].AsUnsignedInteger();
            if (otherAppId == appId)
            {
                // This shouldn't ever happen, but ya never know with Valve. Don't infinite loop.
                Logger.TraceWarning("App {0}, Depot {1} has depotfromapp of {2}!",
                    appId, depotId, otherAppId);
                return INVALID_MANIFEST_ID;
            }

            steam3.RequestAppInfo(otherAppId);

            return GetSteam3DepotManifest(depotId, otherAppId, branch);
        }

        var manifests = depotChild["manifests"];
        var manifests_encrypted = depotChild["encryptedmanifests"];

        if (manifests.Children.Count == 0 && manifests_encrypted.Children.Count == 0)
            return INVALID_MANIFEST_ID;

        var node = manifests[branch]["gid"];

        if (node == KeyValue.Invalid && !string.Equals(branch, DEFAULT_BRANCH, StringComparison.OrdinalIgnoreCase))
        {
            var node_encrypted = manifests_encrypted[branch];
            if (node_encrypted != KeyValue.Invalid)
            {
                var password = Config.BetaPassword;
                while (string.IsNullOrEmpty(password))
                {
                    Logger.TraceInfo("Please enter the password for branch {0}: ", branch);
                    Config.BetaPassword = password = Console.ReadLine();
                }

                var encrypted_gid = node_encrypted["gid"];

                if (encrypted_gid != KeyValue.Invalid)
                {
                    // Submit the password to Steam now to get encryption keys
                    steam3.CheckAppBetaPassword(appId, Config.BetaPassword);

                    if (!steam3.AppBetaPasswords.TryGetValue(branch, out var appBetaPassword))
                    {
                        Logger.TraceError("Password was invalid for branch {0}", branch);
                        return INVALID_MANIFEST_ID;
                    }

                    var input = Utils.DecodeHexString(encrypted_gid.Value);
                    byte[] manifest_bytes;
                    try
                    {
                        manifest_bytes = Utils.SymmetricDecryptECB(input, appBetaPassword);
                    }
                    catch (Exception e)
                    {
                        Logger.TraceError("Failed to decrypt branch {0}: {1}", branch, e.Message);
                        return INVALID_MANIFEST_ID;
                    }

                    return BitConverter.ToUInt64(manifest_bytes, 0);
                }

                Logger.TraceError("Unhandled depot encryption for depotId {0}", depotId);
                return INVALID_MANIFEST_ID;
            }

            return INVALID_MANIFEST_ID;
        }

        if (node.Value == null)
            return INVALID_MANIFEST_ID;

        return ulong.Parse(node.Value);
    }

    static uint GetSteam3AppBuildNumber(uint appId, string branch)
    {
        if (appId == INVALID_APP_ID)
            return 0;


        var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
        var branches = depots["branches"];
        var node = branches[branch];

        if (node == KeyValue.Invalid)
            return 0;

        var buildid = node["buildid"];

        if (buildid == KeyValue.Invalid)
            return 0;

        return uint.Parse(buildid.Value);
    }
    
    static bool AccountHasAccess(uint depotId)
    {
        if (steam3 == null || steam3.steamUser.SteamID == null || (steam3.Licenses == null && steam3.steamUser.SteamID.AccountType != EAccountType.AnonUser))
            return false;

        IEnumerable<uint> licenseQuery;
        if (steam3.steamUser.SteamID.AccountType == EAccountType.AnonUser)
        {
            licenseQuery = [17906];
        }
        else
        {
            licenseQuery = steam3.Licenses.Select(x => x.PackageID).Distinct();
        }

        steam3.RequestPackageInfo(licenseQuery);

        foreach (var license in licenseQuery)
        {
            if (steam3.PackageInfo.TryGetValue(license, out var package) && package != null)
            {
                if (package.KeyValues["appids"].Children.Any(child => child.AsUnsignedInteger() == depotId))
                    return true;

                if (package.KeyValues["depotids"].Children.Any(child => child.AsUnsignedInteger() == depotId))
                    return true;
            }
        }

        return false;
    }
    
    static string GetInstallDir(uint appId)
    {
        var config = GetSteam3AppSection(appId, EAppInfoSection.Config);
        if (config == null)
            return string.Empty;

        return config["installdir"].AsString();
    }
    
    static string GetAppName(uint appId)
    {
        var info = GetSteam3AppSection(appId, EAppInfoSection.Common);
        if (info == null)
            return string.Empty;

        return info["name"].AsString();
    }
    
    static KeyValue GetDepotInfos(uint appId)
    {
        var info = GetSteam3AppSection(appId, EAppInfoSection.Depots);
        if (info == null)
            return null;

        return info;
    }

    static KeyValue GetAllInfo(uint appId)
    {
        KeyValue info = GetSteam3AppSection(appId, EAppInfoSection.All);
        if (info == null)
            return null;

        return info;
    }
    
    internal static KeyValue GetSteam3AppSection(uint appId, EAppInfoSection section)
    {
        if (steam3 == null || steam3.AppInfo == null)
        {
            return null;
        }

        if (!steam3.AppInfo.TryGetValue(appId, out var app) || app == null)
        {
            return null;
        }

        var appinfo = app.KeyValues;
        var section_key = section switch
        {
            EAppInfoSection.Common => "common",
            EAppInfoSection.Extended => "extended",
            EAppInfoSection.Config => "config",
            EAppInfoSection.Depots => "depots",
            EAppInfoSection.All => "",
            _ => throw new NotImplementedException(),
        };

        KeyValue section_kv = null;
        
        if (string.IsNullOrEmpty(section_key))
            return appinfo;
        else
            section_kv = appinfo.Children.Where(c => c.Name == section_key).FirstOrDefault();
        
        return section_kv;
    }
    
    private class GlobalDownloadCounter
    {
        public ulong completeDownloadSize;
        public ulong totalBytesCompressed;
        public ulong totalBytesUncompressed;
    }
    
    private class DepotFilesData
    {
        public DepotDownloadInfo depotDownloadInfo;
        public DepotDownloadCounter depotCounter;
        public string stagingDir;
        public ProtoManifest manifest;
        public ProtoManifest previousManifest;
        public List<ProtoManifest.FileData> filteredFiles;
        public HashSet<string> allFileNames;
    }
    
    private class DepotDownloadCounter
    {
        public ulong completeDownloadSize;
        public ulong sizeDownloaded;
        public ulong depotBytesCompressed;
        public ulong depotBytesUncompressed;
    }
    
    private class FileStreamData
    {
        public FileStream fileStream;
        public SemaphoreSlim fileLock;
        public int chunksToDownload;
    }
    
    private static async Task DownloadSteam3Async(List<DepotDownloadInfo> depots)
    {
        //Ansi.Progress(Ansi.ProgressState.Indeterminate);

        var cts = new CancellationTokenSource();
        cdnPool.ExhaustedToken = cts;

        var downloadCounter = new GlobalDownloadCounter();
        var depotsToDownload = new List<DepotFilesData>(depots.Count);
        var allFileNamesAllDepots = new HashSet<string>();

        // First, fetch all the manifests for each depot (including previous manifests) and perform the initial setup
        foreach (var depot in depots)
        {
            var depotFileData = await ProcessDepotManifestAndFiles(cts, depot, downloadCounter);

            if (depotFileData != null)
            {
                depotsToDownload.Add(depotFileData);
                allFileNamesAllDepots.UnionWith(depotFileData.allFileNames);
            }

            cts.Token.ThrowIfCancellationRequested();
        }

        // If we're about to write all the files to the same directory, we will need to first de-duplicate any files by path
        // This is in last-depot-wins order, from Steam or the list of depots supplied by the user
        if (!string.IsNullOrWhiteSpace(Config.InstallDirectory) && depotsToDownload.Count > 0)
        {
            var claimedFileNames = new HashSet<string>();

            for (var i = depotsToDownload.Count - 1; i >= 0; i--)
            {
                // For each depot, remove all files from the list that have been claimed by a later depot
                depotsToDownload[i].filteredFiles.RemoveAll(file => claimedFileNames.Contains(file.FileName));

                claimedFileNames.UnionWith(depotsToDownload[i].allFileNames);
            }
        }

        foreach (var depotFileData in depotsToDownload)
        {
            await DownloadSteam3AsyncDepotFiles(cts, downloadCounter, depotFileData, allFileNamesAllDepots);
        }

        //Ansi.Progress(Ansi.ProgressState.Hidden);

        Logger.TraceInfo("Total downloaded: {0} bytes ({1} bytes uncompressed) from {2} depots",
            downloadCounter.totalBytesCompressed, downloadCounter.totalBytesUncompressed, depots.Count);
    }
    
    private static async Task DownloadSteam3AsyncDepotFiles(CancellationTokenSource cts,
            GlobalDownloadCounter downloadCounter, DepotFilesData depotFilesData, HashSet<string> allFileNamesAllDepots)
    {
        var depot = depotFilesData.depotDownloadInfo;
        var depotCounter = depotFilesData.depotCounter;

        Logger.TraceInfo("Downloading depot {0}", depot.DepotId);

        var files = depotFilesData.filteredFiles.Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory)).ToArray();
        var networkChunkQueue = new ConcurrentQueue<(FileStreamData fileStreamData, ProtoManifest.FileData fileData, ProtoManifest.ChunkData chunk)>();

        await Utils.InvokeAsync(
            files.Select(file => new Func<Task>(async () =>
                await Task.Run(() => DownloadSteam3AsyncDepotFile(cts, downloadCounter, depotFilesData, file, networkChunkQueue)))),
            maxDegreeOfParallelism: Config.MaxDownloads
        );

        await Utils.InvokeAsync(
            networkChunkQueue.Select(q => new Func<Task>(async () =>
                await Task.Run(() => DownloadSteam3AsyncDepotFileChunk(cts, downloadCounter, depotFilesData,
                    q.fileData, q.fileStreamData, q.chunk)))),
            maxDegreeOfParallelism: Config.MaxDownloads
        );

        // Check for deleted files if updating the depot.
        if (depotFilesData.previousManifest != null)
        {
            var previousFilteredFiles = depotFilesData.previousManifest.Files.AsParallel().Where(f => TestIsFileIncluded(f.FileName)).Select(f => f.FileName).ToHashSet();

            // Check if we are writing to a single output directory. If not, each depot folder is managed independently
            if (string.IsNullOrWhiteSpace(Config.InstallDirectory))
            {
                // Of the list of files in the previous manifest, remove any file names that exist in the current set of all file names
                previousFilteredFiles.ExceptWith(depotFilesData.allFileNames);
            }
            else
            {
                // Of the list of files in the previous manifest, remove any file names that exist in the current set of all file names across all depots being downloaded
                previousFilteredFiles.ExceptWith(allFileNamesAllDepots);
            }

            foreach (var existingFileName in previousFilteredFiles)
            {
                var fileFinalPath = Path.Combine(depot.InstallDir, existingFileName);

                if (!File.Exists(fileFinalPath))
                    continue;

                File.Delete(fileFinalPath);
                Logger.TraceInfo("Deleted {0}", fileFinalPath);
            }
        }

        DepotConfigStore.Instance.InstalledManifestIDs[depot.DepotId] = depot.ManifestId;
        DepotConfigStore.Save();

        Logger.TraceInfo("Depot {0} - Downloaded {1} bytes ({2} bytes uncompressed)", depot.DepotId, depotCounter.depotBytesCompressed, depotCounter.depotBytesUncompressed);
    }
    
    private static async Task<DepotFilesData> ProcessDepotManifestAndFiles(CancellationTokenSource cts, DepotDownloadInfo depot, GlobalDownloadCounter downloadCounter)
    {
        var depotCounter = new DepotDownloadCounter();

        Logger.TraceInfo("Processing depot {0}", depot.DepotId);

        ProtoManifest oldProtoManifest = null;
        ProtoManifest newProtoManifest = null;
        var configDir = Path.Combine(depot.InstallDir, CONFIG_DIR);

        var lastManifestId = INVALID_MANIFEST_ID;
        DepotConfigStore.Instance.InstalledManifestIDs.TryGetValue(depot.DepotId, out lastManifestId);

        // In case we have an early exit, this will force equiv of verifyall next run.
        DepotConfigStore.Instance.InstalledManifestIDs[depot.DepotId] = INVALID_MANIFEST_ID;
        DepotConfigStore.Save();

        if (lastManifestId != INVALID_MANIFEST_ID)
        {
            var oldManifestFileName = Path.Combine(configDir, string.Format("{0}_{1}.bin", depot.DepotId, lastManifestId));

            if (File.Exists(oldManifestFileName))
            {
                byte[] expectedChecksum;

                try
                {
                    expectedChecksum = File.ReadAllBytes(oldManifestFileName + ".sha");
                }
                catch (IOException)
                {
                    expectedChecksum = null;
                }

                oldProtoManifest = ProtoManifest.LoadFromFile(oldManifestFileName, out var currentChecksum);

                if (expectedChecksum == null || !expectedChecksum.SequenceEqual(currentChecksum))
                {
                    // We only have to show this warning if the old manifest ID was different
                    if (lastManifestId != depot.ManifestId)
                        Logger.TraceError("Manifest {0} on disk did not match the expected checksum.", lastManifestId);
                    oldProtoManifest = null;
                }
            }
        }

        if (lastManifestId == depot.ManifestId && oldProtoManifest != null)
        {
            newProtoManifest = oldProtoManifest;
            Logger.TraceInfo("Already have manifest {0} for depot {1}.", depot.ManifestId, depot.DepotId);
        }
        else
        {
            var newManifestFileName = Path.Combine(configDir, string.Format("{0}_{1}.bin", depot.DepotId, depot.ManifestId));
            if (newManifestFileName != null)
            {
                byte[] expectedChecksum;

                try
                {
                    expectedChecksum = File.ReadAllBytes(newManifestFileName + ".sha");
                }
                catch (IOException)
                {
                    expectedChecksum = null;
                }

                newProtoManifest = ProtoManifest.LoadFromFile(newManifestFileName, out var currentChecksum);

                if (newProtoManifest != null && (expectedChecksum == null || !expectedChecksum.SequenceEqual(currentChecksum)))
                {
                    Logger.TraceError("Manifest {0} on disk did not match the expected checksum.", depot.ManifestId);
                    newProtoManifest = null;
                }
            }

            if (newProtoManifest != null)
            {
                Logger.TraceInfo("Already have manifest {0} for depot {1}.", depot.ManifestId, depot.DepotId);
            }
            else
            {
                Logger.TraceInfo("Downloading depot manifest... ");

                DepotManifest depotManifest = null;
                ulong manifestRequestCode = 0;
                var manifestRequestCodeExpiration = DateTime.MinValue;

                do
                {
                    cts.Token.ThrowIfCancellationRequested();

                    Server connection = null;

                    try
                    {
                        connection = cdnPool.GetConnection(cts.Token);

                        string cdnToken = null;
                        if (steam3.CDNAuthTokens.TryGetValue((depot.DepotId, connection.Host), out var authTokenCallbackPromise))
                        {
                            var result = await authTokenCallbackPromise.Task;
                            cdnToken = result.Token;
                        }

                        var now = DateTime.Now;

                        // In order to download this manifest, we need the current manifest request code
                        // The manifest request code is only valid for a specific period in time
                        if (manifestRequestCode == 0 || now >= manifestRequestCodeExpiration)
                        {
                            manifestRequestCode = await steam3.GetDepotManifestRequestCodeAsync(
                                depot.DepotId,
                                depot.AppId,
                                depot.ManifestId,
                                depot.Branch);
                            // This code will hopefully be valid for one period following the issuing period
                            manifestRequestCodeExpiration = now.Add(TimeSpan.FromMinutes(5));

                            // If we could not get the manifest code, this is a fatal error
                            if (manifestRequestCode == 0)
                            {
                                Logger.TraceWarning("No manifest request code was returned for {0} {1}", depot.DepotId, depot.ManifestId);
                                cts.Cancel();
                            }
                        }

                        DebugLog.WriteLine("ContentDownloader",
                            "Downloading manifest {0} from {1} with {2}",
                            depot.ManifestId,
                            connection,
                            cdnPool.ProxyServer != null ? cdnPool.ProxyServer : "no proxy");
                        depotManifest = await cdnPool.CDNClient.DownloadManifestAsync(
                            depot.DepotId,
                            depot.ManifestId,
                            manifestRequestCode,
                            connection,
                            depot.DepotKey,
                            cdnPool.ProxyServer,
                            cdnToken).ConfigureAwait(false);

                        cdnPool.ReturnConnection(connection);
                    }
                    catch (TaskCanceledException)
                    {
                        Logger.TraceWarning("Connection timeout downloading depot manifest {0} {1}. Retrying.", depot.DepotId, depot.ManifestId);
                    }
                    catch (SteamKitWebRequestException e)
                    {
                        // If the CDN returned 403, attempt to get a cdn auth if we didn't yet
                        if (e.StatusCode == HttpStatusCode.Forbidden && !steam3.CDNAuthTokens.ContainsKey((depot.DepotId, connection.Host)))
                        {
                            await steam3.RequestCDNAuthToken(depot.AppId, depot.DepotId, connection);

                            cdnPool.ReturnConnection(connection);

                            continue;
                        }

                        cdnPool.ReturnBrokenConnection(connection);

                        if (e.StatusCode == HttpStatusCode.Unauthorized || e.StatusCode == HttpStatusCode.Forbidden)
                        {
                            Logger.TraceError("Encountered {2} for depot manifest {0} {1}. Aborting.", depot.DepotId, depot.ManifestId, (int)e.StatusCode);
                            break;
                        }

                        if (e.StatusCode == HttpStatusCode.NotFound)
                        {
                            Logger.TraceError("Encountered 404 for depot manifest {0} {1}. Aborting.", depot.DepotId, depot.ManifestId);
                            break;
                        }

                        Logger.TraceError("Encountered error downloading depot manifest {0} {1}: {2}", depot.DepotId, depot.ManifestId, e.StatusCode);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        cdnPool.ReturnBrokenConnection(connection);
                        Logger.TraceError("Encountered error downloading manifest for depot {0} {1}: {2}", depot.DepotId, depot.ManifestId, e.Message);
                    }
                } while (depotManifest == null);

                if (depotManifest == null)
                {
                    Logger.TraceError("\nUnable to download manifest {0} for depot {1}", depot.ManifestId, depot.DepotId);
                    cts.Cancel();
                }

                // Throw the cancellation exception if requested so that this task is marked failed
                cts.Token.ThrowIfCancellationRequested();


                newProtoManifest = new ProtoManifest(depotManifest, depot.ManifestId);
                newProtoManifest.SaveToFile(newManifestFileName, out var checksum);
                File.WriteAllBytes(newManifestFileName + ".sha", checksum);

                Logger.TraceInfo("Done!");
            }
        }

        newProtoManifest.Files.Sort((x, y) => string.Compare(x.FileName, y.FileName, StringComparison.Ordinal));

        Logger.TraceInfo("Manifest {0} ({1})", depot.ManifestId, newProtoManifest.CreationTime);
        

        var stagingDir = Path.Combine(depot.InstallDir, STAGING_DIR);

        var filesAfterExclusions = newProtoManifest.Files.AsParallel().Where(f => TestIsFileIncluded(f.FileName)).ToList();
        var allFileNames = new HashSet<string>(filesAfterExclusions.Count);

        // Pre-process
        filesAfterExclusions.ForEach(file =>
        {
            allFileNames.Add(file.FileName);

            var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
            var fileStagingPath = Path.Combine(stagingDir, file.FileName);

            if (file.Flags.HasFlag(EDepotFileFlag.Directory))
            {
                Directory.CreateDirectory(fileFinalPath);
                Directory.CreateDirectory(fileStagingPath);
            }
            else
            {
                // Some manifests don't explicitly include all necessary directories
                Directory.CreateDirectory(Path.GetDirectoryName(fileFinalPath));
                Directory.CreateDirectory(Path.GetDirectoryName(fileStagingPath));

                downloadCounter.completeDownloadSize += file.TotalSize;
                depotCounter.completeDownloadSize += file.TotalSize;
            }
        });

        return new DepotFilesData
        {
            depotDownloadInfo = depot,
            depotCounter = depotCounter,
            stagingDir = stagingDir,
            manifest = newProtoManifest,
            previousManifest = oldProtoManifest,
            filteredFiles = filesAfterExclusions,
            allFileNames = allFileNames
        };
    }
    
    private static void DownloadSteam3AsyncDepotFile(
            CancellationTokenSource cts,
            GlobalDownloadCounter downloadCounter,
            DepotFilesData depotFilesData,
            ProtoManifest.FileData file,
            ConcurrentQueue<(FileStreamData, ProtoManifest.FileData, ProtoManifest.ChunkData)> networkChunkQueue)
    {
        cts.Token.ThrowIfCancellationRequested();

        var depot = depotFilesData.depotDownloadInfo;
        var stagingDir = depotFilesData.stagingDir;
        var depotDownloadCounter = depotFilesData.depotCounter;
        var oldProtoManifest = depotFilesData.previousManifest;
        ProtoManifest.FileData oldManifestFile = null;
        if (oldProtoManifest != null)
        {
            oldManifestFile = oldProtoManifest.Files.SingleOrDefault(f => f.FileName == file.FileName);
        }

        var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
        var fileStagingPath = Path.Combine(stagingDir, file.FileName);

        // This may still exist if the previous run exited before cleanup
        if (File.Exists(fileStagingPath))
        {
            File.Delete(fileStagingPath);
        }

        List<ProtoManifest.ChunkData> neededChunks;
        var fi = new FileInfo(fileFinalPath);
        var fileDidExist = fi.Exists;
        if (!fileDidExist)
        {
            Logger.Trace("Pre-allocating {0}", fileFinalPath);

            // create new file. need all chunks
            using var fs = File.Create(fileFinalPath);
            try
            {
                fs.SetLength((long)file.TotalSize);
            }
            catch (IOException ex)
            {
                throw new Exception(string.Format("Failed to allocate file {0}: {1}", fileFinalPath, ex.Message));
            }

            neededChunks = new List<ProtoManifest.ChunkData>(file.Chunks);
        }
        else
        {
            // open existing
            if (oldManifestFile != null)
            {
                neededChunks = [];

                var hashMatches = oldManifestFile.FileHash.SequenceEqual(file.FileHash);
                if (Config.Verify || !hashMatches)
                {
                    // we have a version of this file, but it doesn't fully match what we want
                    if (Config.Verify)
                    {
                        Logger.TraceInfo("Validating {0}", fileFinalPath);
                    }

                    var matchingChunks = new List<ChunkMatch>();

                    foreach (var chunk in file.Chunks)
                    {
                        var oldChunk = oldManifestFile.Chunks.FirstOrDefault(c => c.ChunkID.SequenceEqual(chunk.ChunkID));
                        if (oldChunk != null)
                        {
                            matchingChunks.Add(new ChunkMatch(oldChunk, chunk));
                        }
                        else
                        {
                            neededChunks.Add(chunk);
                        }
                    }

                    var orderedChunks = matchingChunks.OrderBy(x => x.OldChunk.Offset);

                    var copyChunks = new List<ChunkMatch>();

                    using (var fsOld = File.Open(fileFinalPath, FileMode.Open))
                    {
                        foreach (var match in orderedChunks)
                        {
                            fsOld.Seek((long)match.OldChunk.Offset, SeekOrigin.Begin);

                            var adler = Utils.AdlerHash(fsOld, (int)match.OldChunk.UncompressedLength);
                            if (!adler.SequenceEqual(match.OldChunk.Checksum))
                            {
                                neededChunks.Add(match.NewChunk);
                            }
                            else
                            {
                                copyChunks.Add(match);
                            }
                        }
                    }

                    if (!hashMatches || neededChunks.Count > 0)
                    {
                        File.Move(fileFinalPath, fileStagingPath);

                        using (var fsOld = File.Open(fileStagingPath, FileMode.Open))
                        {
                            using var fs = File.Open(fileFinalPath, FileMode.Create);
                            try
                            {
                                fs.SetLength((long)file.TotalSize);
                            }
                            catch (IOException ex)
                            {
                                throw new Exception(string.Format("Failed to resize file to expected size {0}: {1}", fileFinalPath, ex.Message));
                            }

                            foreach (var match in copyChunks)
                            {
                                fsOld.Seek((long)match.OldChunk.Offset, SeekOrigin.Begin);

                                var tmp = new byte[match.OldChunk.UncompressedLength];
                                fsOld.Read(tmp, 0, tmp.Length);

                                fs.Seek((long)match.NewChunk.Offset, SeekOrigin.Begin);
                                fs.Write(tmp, 0, tmp.Length);
                            }
                        }

                        File.Delete(fileStagingPath);
                    }
                }
            }
            else
            {
                // No old manifest or file not in old manifest. We must validate.

                using var fs = File.Open(fileFinalPath, FileMode.Open);
                if ((ulong)fi.Length != file.TotalSize)
                {
                    try
                    {
                        fs.SetLength((long)file.TotalSize);
                    }
                    catch (IOException ex)
                    {
                        throw new Exception(string.Format("Failed to allocate file {0}: {1}", fileFinalPath, ex.Message));
                    }
                }

                Logger.TraceInfo("Validating {0}", fileFinalPath);
                neededChunks = Utils.ValidateSteam3FileChecksums(fs, [.. file.Chunks.OrderBy(x => x.Offset)]);
            }

            if (neededChunks.Count == 0)
            {
                lock (depotDownloadCounter)
                {
                    depotDownloadCounter.sizeDownloaded += file.TotalSize;
                    Logger.Trace("{0,6:#00.00}% {1}", (depotDownloadCounter.sizeDownloaded / (float)depotDownloadCounter.completeDownloadSize) * 100.0f, fileFinalPath);
                }

                lock (downloadCounter)
                {
                    downloadCounter.completeDownloadSize -= file.TotalSize;
                }

                return;
            }

            var sizeOnDisk = (file.TotalSize - (ulong)neededChunks.Select(x => (long)x.UncompressedLength).Sum());
            lock (depotDownloadCounter)
            {
                depotDownloadCounter.sizeDownloaded += sizeOnDisk;
            }

            lock (downloadCounter)
            {
                downloadCounter.completeDownloadSize -= sizeOnDisk;
            }
        }

        var fileIsExecutable = file.Flags.HasFlag(EDepotFileFlag.Executable);
        if (fileIsExecutable && (!fileDidExist || oldManifestFile == null || !oldManifestFile.Flags.HasFlag(EDepotFileFlag.Executable)))
        {
            //PlatformUtilities.SetExecutable(fileFinalPath, true);
        }
        else if (!fileIsExecutable && oldManifestFile != null && oldManifestFile.Flags.HasFlag(EDepotFileFlag.Executable))
        {
            //PlatformUtilities.SetExecutable(fileFinalPath, false);
        }

        var fileStreamData = new FileStreamData
        {
            fileStream = null,
            fileLock = new SemaphoreSlim(1),
            chunksToDownload = neededChunks.Count
        };

        foreach (var chunk in neededChunks)
        {
            networkChunkQueue.Enqueue((fileStreamData, file, chunk));
        }
    }
    
    private class ChunkMatch(ProtoManifest.ChunkData oldChunk, ProtoManifest.ChunkData newChunk)
    {
        public ProtoManifest.ChunkData OldChunk { get; } = oldChunk;
        public ProtoManifest.ChunkData NewChunk { get; } = newChunk;
    }
    
    private static async Task DownloadSteam3AsyncDepotFileChunk(
            CancellationTokenSource cts,
            GlobalDownloadCounter downloadCounter,
            DepotFilesData depotFilesData,
            ProtoManifest.FileData file,
            FileStreamData fileStreamData,
            ProtoManifest.ChunkData chunk)
    {
        cts.Token.ThrowIfCancellationRequested();

        var depot = depotFilesData.depotDownloadInfo;
        var depotDownloadCounter = depotFilesData.depotCounter;

        var chunkID = Convert.ToHexString(chunk.ChunkID).ToLowerInvariant();

        var data = new DepotManifest.ChunkData
        {
            ChunkID = chunk.ChunkID,
            Checksum = BitConverter.ToUInt32(chunk.Checksum),
            Offset = chunk.Offset,
            CompressedLength = chunk.CompressedLength,
            UncompressedLength = chunk.UncompressedLength
        };

        var written = 0;
        var chunkBuffer = ArrayPool<byte>.Shared.Rent((int)data.UncompressedLength);

        try
        {
            do
            {
                cts.Token.ThrowIfCancellationRequested();

                Server connection = null;

                try
                {
                    connection = cdnPool.GetConnection(cts.Token);

                    string cdnToken = null;
                    if (steam3.CDNAuthTokens.TryGetValue((depot.DepotId, connection.Host), out var authTokenCallbackPromise))
                    {
                        var result = await authTokenCallbackPromise.Task;
                        cdnToken = result.Token;
                    }

                    DebugLog.WriteLine("ContentDownloader", "Downloading chunk {0} from {1} with {2}", chunkID, connection, cdnPool.ProxyServer != null ? cdnPool.ProxyServer : "no proxy");
                    written = await cdnPool.CDNClient.DownloadDepotChunkAsync(
                        depot.DepotId,
                        data,
                        connection,
                        chunkBuffer,
                        depot.DepotKey,
                        cdnPool.ProxyServer,
                        cdnToken).ConfigureAwait(false);

                    cdnPool.ReturnConnection(connection);

                    break;
                }
                catch (TaskCanceledException)
                {
                    Logger.TraceError("Connection timeout downloading chunk {0}", chunkID);
                }
                catch (SteamKitWebRequestException e)
                {
                    // If the CDN returned 403, attempt to get a cdn auth if we didn't yet,
                    // if auth task already exists, make sure it didn't complete yet, so that it gets awaited above
                    if (e.StatusCode == HttpStatusCode.Forbidden &&
                        (!steam3.CDNAuthTokens.TryGetValue((depot.DepotId, connection.Host), out var authTokenCallbackPromise) || !authTokenCallbackPromise.Task.IsCompleted))
                    {
                        await steam3.RequestCDNAuthToken(depot.AppId, depot.DepotId, connection);

                        cdnPool.ReturnConnection(connection);

                        continue;
                    }

                    cdnPool.ReturnBrokenConnection(connection);

                    if (e.StatusCode == HttpStatusCode.Unauthorized || e.StatusCode == HttpStatusCode.Forbidden)
                    {
                        Logger.TraceError("Encountered {1} for chunk {0}. Aborting.", chunkID, (int)e.StatusCode);
                        break;
                    }

                    Logger.TraceError("Encountered error downloading chunk {0}: {1}", chunkID, e.StatusCode);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    cdnPool.ReturnBrokenConnection(connection);
                    Logger.TraceError("Encountered unexpected error downloading chunk {0}: {1}", chunkID, e.Message);
                }
            } while (written == 0);

            if (written == 0)
            {
                Logger.TraceError("Failed to find any server with chunk {0} for depot {1}. Aborting.", chunkID, depot.DepotId);
                cts.Cancel();
            }

            // Throw the cancellation exception if requested so that this task is marked failed
            cts.Token.ThrowIfCancellationRequested();

            try
            {
                await fileStreamData.fileLock.WaitAsync().ConfigureAwait(false);

                if (fileStreamData.fileStream == null)
                {
                    var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
                    fileStreamData.fileStream = File.Open(fileFinalPath, FileMode.Open);
                }

                fileStreamData.fileStream.Seek((long)data.Offset, SeekOrigin.Begin);
                await fileStreamData.fileStream.WriteAsync(chunkBuffer.AsMemory(0, written), cts.Token);
            }
            finally
            {
                fileStreamData.fileLock.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunkBuffer);
        }

        var remainingChunks = Interlocked.Decrement(ref fileStreamData.chunksToDownload);
        if (remainingChunks == 0)
        {
            fileStreamData.fileStream?.Dispose();
            fileStreamData.fileLock.Dispose();
        }

        ulong sizeDownloaded = 0;
        lock (depotDownloadCounter)
        {
            sizeDownloaded = depotDownloadCounter.sizeDownloaded + (ulong)written;
            depotDownloadCounter.sizeDownloaded = sizeDownloaded;
            depotDownloadCounter.depotBytesCompressed += chunk.CompressedLength;
            depotDownloadCounter.depotBytesUncompressed += chunk.UncompressedLength;
        }

        lock (downloadCounter)
        {
            downloadCounter.totalBytesCompressed += chunk.CompressedLength;
            downloadCounter.totalBytesUncompressed += chunk.UncompressedLength;
            
        }

        if (remainingChunks == 0)
        {
            var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
            Logger.Trace("{0,6:#00.00}% {1}", (sizeDownloaded / (float)depotDownloadCounter.completeDownloadSize) * 100.0f, fileFinalPath);
        }
    }
    
    static bool TestIsFileIncluded(string filename)
    {
        return true;
    }
    
}