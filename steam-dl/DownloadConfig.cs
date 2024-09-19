namespace steam_dl;

public class DownloadConfig
{
    public int CellID { get; set; }
    
    public string InstallDirectory { get; set; }
    
    public bool Verify { get; set; }
    public bool RememberPassword { get; set; }
    public string Platform { get; set; }
    public string Architecture { get; set; }
    public string Branch { get; set; }
    public string Language { get; set; }

    // A Steam LoginID to allow multiple concurrent connections
    public uint? LoginID { get; set; }
    public int MaxServers { get; set; } = 20;
    public int MaxDownloads { get; set; } = 8;
    public string BetaPassword { get; set; }
}