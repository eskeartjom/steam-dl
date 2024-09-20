steam-dl
===============
steam-dl is an app to download games directly from Steam without having to install the Steam client.

steam-dl utilizing the [SteamKit2](https://github.com/SteamRE/SteamKit) library and is based on the [DepotDownloader](https://github.com/SteamRE/DepotDownloader)

## How to build

Clone the repository

```shell
git clone https://github.com/eskeartjom/steam-dl
cd steam-dl
```

### Build for Windows

```powershell
.\BuildWin64.ps1
```

### Build for macOS

```shell
chmod +x ./BuildMacOS64.sh
./BuildMacOS64.sh
```

### Build for Linux

```shell
chmod +x ./BuildLinux64.sh
./BuildLinux64.sh
```

The executable will be in the Build directory.

## Usage

### Login to steam
```shell
./steam-dl --login -u <username> [-p <password>] --remember
```


### Downloading an app
```shell
./steam-dl -a <id> -u <username> [-p <password>] [other options]
```

For example: `./steam-dl -a 257850 -u testuser`

## Parameters

Parameter                   | Description
--------------------------- | -----------
`-a/--appid <#>`		    | the AppID to download.
`--os <os>`				    | the operating system for which to download the game (windows, macos or linux, default: OS the program is currently running on)
`--arch <arch>`		        | the architecture for which to download the game (32 or 64, default: the host's architecture)
`--language <lang>`		    | the language for which to download the game (default: english)
`-u/--username <user>`	    | the username of the account to login to for restricted content.
`-p/--password <pass>`	    | the password of the account to login to for restricted content.
`-r/--remember`	            | if set, remember the password for subsequent logins of this user.
`-l/--login`	            | if set, you will just be able to test login into steam. No downloads will be made
`-o/--output <installdir>`  | the directory in which to place downloaded files.
`--verify`				    | Include checksum verification of files already downloaded
`-v/--version`              | print version

## For your information

### Why am I prompted to enter a 2-factor code every time I run the app?
Your 2-factor code authenticates a Steam session. You need to "remember" your session with `-r` or `--remember` which persists the login key for your Steam session.
