﻿using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using SteamLibrary.Models;
using SteamLibrary.Services;
using SteamKit2;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using Playnite;
using System.Windows;
using System.Reflection;
using System.Collections.ObjectModel;
using Playnite.Common.Web;
using Steam;
using System.Diagnostics;

namespace SteamLibrary
{
    public class SteamLibrary : LibraryPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly Configuration config;
        private const string dbImportMessageId = "steamlibImportError";

        internal SteamLibrarySettings LibrarySettings { get; private set; }
        internal SteamServicesClient ServicesClient;

        public SteamLibrary(IPlayniteAPI api) : base(api)
        {
            Initialize(api);
            config = GetPluginConfiguration<Configuration>();
            ServicesClient = new SteamServicesClient(config.ServicesEndpoint, api.ApplicationInfo.ApplicationVersion);
        }

        public SteamLibrary(IPlayniteAPI api, SteamServicesClient client) : base(api)
        {
            Initialize(api);
            ServicesClient = client;
        }

        private void Initialize(IPlayniteAPI api)
        {
            LibrarySettings = new SteamLibrarySettings(this, PlayniteApi)
            {
                SteamUsers = GetSteamUsers()
            };
        }

        internal static GameAction CreatePlayTask(GameID gameId)
        {
            return new GameAction()
            {
                Name = "Play",
                Type = GameActionType.URL,
                Path = @"steam://rungameid/" + gameId,
                IsHandledByPlugin = true
            };
        }

        internal GameInfo GetInstalledGameFromFile(string path)
        {
            var kv = new KeyValue();
            kv.ReadFileAsText(path);

            var name = string.Empty;
            if (string.IsNullOrEmpty(kv["name"].Value))
            {
                if (kv["UserConfig"]["name"].Value != null)
                {
                    name = StringExtensions.NormalizeGameName(kv["UserConfig"]["name"].Value);
                }
            }
            else
            {
                name = StringExtensions.NormalizeGameName(kv["name"].Value);
            }

            var gameId = new GameID(kv["appID"].AsUnsignedInteger());
            var installDir = Path.Combine((new FileInfo(path)).Directory.FullName, "common", kv["installDir"].Value);
            if (!Directory.Exists(installDir))
            {
                installDir = Path.Combine((new FileInfo(path)).Directory.FullName, "music", kv["installDir"].Value);
                if (!Directory.Exists(installDir))
                {
                    installDir = string.Empty;
                }
            }
            
            var branch = kv["UserConfig"]["betakey"].AsString();
            if (branch.IsNullOrEmpty())
            {
                //Failsafe
                branch = "public";
            }
            var state = kv["StateFlags"].AsUnsignedInteger();
            var outdated = (state & 2) == 2; //check outdated flag is set bitwise

            if (outdated)
            {
                Console.WriteLine("hi");
            }

            var game = new GameInfo()
            {
                Source = "Steam",
                GameId = gameId.ToString(),
                Name = name.RemoveTrademarks(),
                InstallDirectory = installDir,
                PlayAction = CreatePlayTask(gameId),
                IsInstalled = true,
                Platform = "PC",
                Version = kv["buildid"].AsUnsignedInteger().ToString(),
                Branch = branch,
                Outdated = outdated,
            };

            return game;
        }

        internal List<GameInfo> GetInstalledGamesFromFolder(string path)
        {
            var games = new List<GameInfo>();

            foreach (var file in Directory.GetFiles(path, @"appmanifest*"))
            {
                try
                {
                    var game = GetInstalledGameFromFile(Path.Combine(path, file));
                    if (game.InstallDirectory.IsNullOrEmpty() || game.InstallDirectory.Contains(@"steamapps\music"))
                    {
                        logger.Info($"Steam game {game.Name} is not properly installed or it's a soundtrack, skipping.");
                        continue;
                    }

                    games.Add(game);
                }
                catch (Exception exc)
                {
                    // Steam can generate invalid acf file according to issue #37
                    logger.Error(exc, $"Failed to get information about installed game from: {file}");
                }
            }

            return games;
        }

        internal List<GameInfo> GetInstalledGoldSrcModsFromFolder(string path)
        {
            var games = new List<GameInfo>();
            var firstPartyMods = new string[] { "bshift", "cstrike", "czero", "czeror", "dmc", "dod", "gearbox", "ricochet", "tfc", "valve" };
            var dirInfo = new DirectoryInfo(path);

            foreach (var folder in dirInfo.GetDirectories().Where(a => !firstPartyMods.Contains(a.Name)).Select(a => a.FullName))
            {
                try
                {
                    var game = GetInstalledModFromFolder(folder, ModInfo.ModType.HL);
                    if (game != null)
                    {
                        games.Add(game);
                    }
                }
                catch (Exception exc)
                {
                    // gameinfo.txt may not exist or may be invalid
                    logger.Error(exc, $"Failed to get information about installed GoldSrc mod from: {path}");
                }
            }

            return games;
        }

        internal List<GameInfo> GetInstalledSourceModsFromFolder(string path)
        {
            var games = new List<GameInfo>();

            foreach (var folder in Directory.GetDirectories(path))
            {
                try
                {
                    var game = GetInstalledModFromFolder(folder, ModInfo.ModType.HL2);
                    if (game != null)
                    {
                        games.Add(game);
                    }
                }
                catch (Exception exc)
                {
                    // gameinfo.txt may not exist or may be invalid
                    logger.Error(exc, $"Failed to get information about installed Source mod from: {path}");
                }
            }

            return games;
        }

        internal GameInfo GetInstalledModFromFolder(string path, ModInfo.ModType modType)
        {
            var modInfo = ModInfo.GetFromFolder(path, modType);
            if (modInfo == null)
            {
                return null;
            }

            var game = new GameInfo()
            {
                Source = "Steam",
                GameId = modInfo.GameId.ToString(),
                Name = modInfo.Name.RemoveTrademarks(),
                InstallDirectory = path,
                PlayAction = CreatePlayTask(modInfo.GameId),
                IsInstalled = true,
                Developers = new List<string>() { modInfo.Developer },
                Links = modInfo.Links,
                Tags = modInfo.Categories,
                Icon = modInfo.IconPath,
                Platform = "PC"
            };

            return game;
        }

        internal Dictionary<string, GameInfo> GetInstalledGames(bool includeMods = true)
        {
            var games = new Dictionary<string, GameInfo>();
            if (!Steam.IsInstalled)
            {
                return games;
            }

            foreach (var folder in GetLibraryFolders())
            {
                var libFolder = Path.Combine(folder, "steamapps");
                if (Directory.Exists(libFolder))
                {
                    GetInstalledGamesFromFolder(libFolder).ForEach(a =>
                    {
                        // Ignore redist
                        if (a.GameId == "228980")
                        {
                            return;
                        }

                        if (!games.ContainsKey(a.GameId))
                        {
                            games.Add(a.GameId, a);
                        }
                    });
                }
                else
                {
                    logger.Warn($"Steam library {libFolder} not found.");
                }
            }

            if (includeMods)
            {
                try
                {
                    // In most cases, this will be inside the folder where Half-Life is installed.
                    var modInstallPath = Steam.ModInstallPath;
                    if (!string.IsNullOrEmpty(modInstallPath) && Directory.Exists(modInstallPath))
                    {
                        GetInstalledGoldSrcModsFromFolder(Steam.ModInstallPath).ForEach(a =>
                        {
                            if (!games.ContainsKey(a.GameId))
                            {
                                games.Add(a.GameId, a);
                            }
                        });
                    }

                    // In most cases, this will be inside the library folder where Steam is installed.
                    var sourceModInstallPath = Steam.SourceModInstallPath;
                    if (!string.IsNullOrEmpty(sourceModInstallPath) && Directory.Exists(sourceModInstallPath))
                    {
                        GetInstalledSourceModsFromFolder(Steam.SourceModInstallPath).ForEach(a =>
                        {
                            if (!games.ContainsKey(a.GameId))
                            {
                                games.Add(a.GameId, a);
                            }
                        });
                    }
                }
                catch (Exception e) when (!Environment.IsDebugBuild)
                {
                    logger.Error(e, "Failed to import Steam mods.");
                }
            }

            return games;
        }

        internal static List<string> GetLibraryFolders(KeyValue foldersData)
        {
            var dbs = new List<string>();
            foreach (var child in foldersData.Children)
            {
                if (int.TryParse(child.Name, out int _))
                {
                    if (!child.Value.IsNullOrEmpty())
                    {
                        dbs.Add(child.Value);
                    }
                    else if (child.Children.HasItems())
                    {
                        var path = child.Children.FirstOrDefault(a => a.Name?.Equals("path", StringComparison.OrdinalIgnoreCase) == true);
                        if (!path.Value.IsNullOrEmpty())
                        {
                            dbs.Add(path.Value);
                        }
                    }
                }
            }

            return dbs;
        }

        internal static List<string> GetLibraryFolders()
        {
            var dbs = new List<string>() { Steam.InstallationPath };
            var configPath = Path.Combine(Steam.InstallationPath, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(configPath))
            {
                return dbs;
            }

            try
            {
                using (var fs = new FileStream(configPath, FileMode.Open, FileAccess.Read))
                {
                    var kv = new KeyValue();
                    kv.ReadAsText(fs);
                    foreach (var dir in GetLibraryFolders(kv))
                    {
                        if (Directory.Exists(dir))
                        {
                            dbs.Add(dir);
                        }
                        else
                        {
                            logger.Warn($"Found external Steam directory, but path doesn't exists: {dir}");
                        }
                    }
                }
            }
            catch (Exception e) when (!Debugger.IsAttached)
            {
                logger.Error(e, "Failed to get additional Steam library folders.");
            }

            return dbs;
        }

        internal List<LocalSteamUser> GetSteamUsers()
        {
            var users = new List<LocalSteamUser>();
            if (File.Exists(Steam.LoginUsersPath))
            {
                var config = new KeyValue();

                try
                {
                    config.ReadFileAsText(Steam.LoginUsersPath);
                    foreach (var user in config.Children)
                    {
                        users.Add(new LocalSteamUser()
                        {
                            Id = ulong.Parse(user.Name),
                            AccountName = user["AccountName"].Value,
                            PersonaName = user["PersonaName"].Value,
                            Recent = user["mostrecent"].AsBoolean()
                        });
                    }
                }
                catch (Exception e) when (!Environment.IsDebugBuild)
                {
                    logger.Error(e, "Failed to get list of local users.");
                }
            }

            return users;
        }

        internal List<GameInfo> GetLibraryGames(SteamLibrarySettings settings)
        {
            if (settings.UserId.IsNullOrEmpty())
            {
                throw new Exception(PlayniteApi.Resources.GetString("LOCNotLoggedInError"));
            }

            var userId = ulong.Parse(settings.UserId);
            if (settings.IsPrivateAccount)
            {
                return GetLibraryGames(userId, GetPrivateOwnedGames(userId, LibrarySettings.ApiKey, LibrarySettings.IncludeFreeSubGames)?.response?.games);
            }
            else
            {
                return GetLibraryGames(userId, ServicesClient.GetSteamLibrary(userId, LibrarySettings.IncludeFreeSubGames));
            }
        }

        internal GetOwnedGamesResult GetPrivateOwnedGames(ulong userId, string apiKey, bool freeSub)
        {
            var libraryUrl = @"https://api.steampowered.com/IPlayerService/GetOwnedGames/v0001/?key={0}&include_appinfo=1&include_played_free_games=1&format=json&steamid={1}&skip_unvetted_apps=0";
            if (freeSub)
            {
                libraryUrl += "&include_free_sub=1";
            }

            var stringLibrary = HttpDownloader.DownloadString(string.Format(libraryUrl, apiKey, userId));
            return JsonConvert.DeserializeObject<GetOwnedGamesResult>(stringLibrary);
        }

        internal List<GameInfo> GetLibraryGames(ulong userId, List<GetOwnedGamesResult.Game> ownedGames)
        {
            if (ownedGames == null)
            {
                throw new Exception("No games found on specified Steam account.");
            }

            IDictionary<string, DateTime> lastActivity = null;
            try
            {
                lastActivity = GetGamesLastActivity(userId);
            }
            catch (Exception exc)
            {
                logger.Warn(exc, "Failed to import Steam last activity.");
            }

            var games = new List<GameInfo>();
            foreach (var game in ownedGames)
            {
                // Ignore games without name, like 243870
                if (string.IsNullOrEmpty(game.name))
                {
                    continue;
                }

                var newGame = new GameInfo()
                {
                    Source = "Steam",
                    Name = game.name.RemoveTrademarks(),
                    GameId = game.appid.ToString(),
                    Playtime = game.playtime_forever * 60,
                    CompletionStatus = game.playtime_forever > 0 ? CompletionStatus.Played : CompletionStatus.NotPlayed,
                    Platform = "PC"
                };

                if (lastActivity != null && lastActivity.TryGetValue(newGame.GameId, out var gameLastActivity))
                {
                    newGame.LastActivity = gameLastActivity;
                }

                games.Add(newGame);
            }

            return games;
        }

        public IDictionary<string, DateTime> GetGamesLastActivity(ulong steamId)
        {
            var id = new SteamID(steamId);
            var result = new Dictionary<string, DateTime>();
            var vdf = Path.Combine(Steam.InstallationPath, "userdata", id.AccountID.ToString(), "config", "localconfig.vdf");
            var sharedconfig = new KeyValue();
            sharedconfig.ReadFileAsText(vdf);

            var apps = sharedconfig["Software"]["Valve"]["Steam"]["apps"];
            foreach (var app in apps.Children)
            {
                if (app.Children.Count == 0)
                {
                    continue;
                }

                string gameId = app.Name;
                if (app.Name.Contains('_'))
                {
                    // Mods are keyed differently, "<appId>_<modId>"
                    // Ex. 215_2287856061
                    string[] parts = app.Name.Split('_');
                    if (uint.TryParse(parts[0], out uint appId) && uint.TryParse(parts[1], out uint modId))
                    {
                        var gid = new GameID()
                        {
                            AppID = appId,
                            AppType = GameID.GameType.GameMod,
                            ModID = modId
                        };
                        gameId = gid;
                    }
                    else
                    {
                        // Malformed app id?
                        continue;
                    }
                }

                result.Add(gameId, DateTimeOffset.FromUnixTimeSeconds(app["LastPlayed"].AsLong()).LocalDateTime);
            }

            return result;
        }

        public void ImportSteamLastActivity(ulong accountId)
        {
            var dialogs = PlayniteApi.Dialogs;
            var resources = PlayniteApi.Resources;
            var db = PlayniteApi.Database;

            if (accountId == 0)
            {
                dialogs.ShowMessage(
                    resources.GetString("LOCSettingsSteamLastActivityImportErrorAccount"),
                    resources.GetString("LOCImportError"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!db.IsOpen)
            {
                dialogs.ShowMessage(
                    resources.GetString("LOCSettingsSteamLastActivityImportErrorDb"),
                    resources.GetString("LOCImportError"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                using (db.BufferedUpdate())
                {
                    foreach (var kvp in GetGamesLastActivity(accountId))
                    {
                        var dbGame = db.Games.FirstOrDefault(a => a.PluginId == Id && a.GameId == kvp.Key);
                        if (dbGame == null)
                        {
                            continue;
                        }

                        if (dbGame.LastActivity >= kvp.Value)
                        {
                            continue;
                        }
                        dbGame.LastActivity = kvp.Value;
                    }
                }

                dialogs.ShowMessage(resources.GetString("LOCImportCompleted"));
            }
            catch (Exception exc) when (!Environment.IsDebugBuild)
            {
                logger.Error(exc, "Failed to import Steam last activity.");
                dialogs.ShowMessage(
                    resources.GetString("LOCSettingsSteamLastActivityImportError"),
                    resources.GetString("LOCImportError"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public List<GameInfo> GetCategorizedGames(ulong steamId)
        {
            var id = new SteamID(steamId);
            var result = new List<GameInfo>();
            var vdf = Path.Combine(Steam.InstallationPath, "userdata", id.AccountID.ToString(), "7", "remote", "sharedconfig.vdf");
            var sharedconfig = new KeyValue();
            sharedconfig.ReadFileAsText(vdf);

            var apps = sharedconfig["Software"]["Valve"]["Steam"]["apps"];
            foreach (var app in apps.Children)
            {
                if (app.Children.Count == 0)
                {
                    continue;
                }

                var appData = new List<string>();
                var isFavorite = false;
                foreach (var tag in app["tags"].Children)
                {
                    if (tag.Value == "favorite")
                    {
                        isFavorite = true;
                    }
                    else
                    {
                        appData.Add(tag.Value);
                    }
                }

                string gameId = app.Name;
                if (app.Name.Contains('_'))
                {
                    // Mods are keyed differently, "<appId>_<modId>"
                    // Ex. 215_2287856061
                    string[] parts = app.Name.Split('_');
                    if (uint.TryParse(parts[0], out uint appId) && uint.TryParse(parts[1], out uint modId))
                    {
                        var gid = new GameID()
                        {
                            AppID = appId,
                            AppType = GameID.GameType.GameMod,
                            ModID = modId
                        };
                        gameId = gid;
                    }
                    else
                    {
                        // Malformed app id?
                        continue;
                    }
                }

                result.Add(new GameInfo()
                {
                    Source = "Steam",
                    GameId = gameId,
                    Categories = new List<string>(appData),
                    Hidden = app["hidden"].AsInteger() == 1,
                    Favorite = isFavorite
                });
            }

            return result;
        }

        public void ImportSteamCategories(ulong accountId)
        {
            var dialogs = PlayniteApi.Dialogs;
            var resources = PlayniteApi.Resources;
            var db = PlayniteApi.Database;

            if (dialogs.ShowMessage(
                resources.GetString("LOCSettingsSteamCatImportWarn"),
                resources.GetString("LOCSettingsSteamCatImportWarnTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            if (accountId == 0)
            {
                dialogs.ShowMessage(
                    resources.GetString("LOCSettingsSteamCatImportErrorAccount"),
                    resources.GetString("LOCImportError"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!db.IsOpen)
            {
                dialogs.ShowMessage(
                    resources.GetString("LOCSettingsSteamCatImportErrorDb"),
                    resources.GetString("LOCImportError"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                using (db.BufferedUpdate())
                {
                    foreach (var game in GetCategorizedGames(accountId))
                    {
                        var dbGame = db.Games.FirstOrDefault(a => a.PluginId == Id && a.GameId == game.GameId);
                        if (dbGame == null)
                        {
                            continue;
                        }

                        if (game.Categories.HasItems())
                        {
                            dbGame.CategoryIds = db.Categories.Add(game.Categories).Select(a => a.Id).ToList();
                        }

                        if (game.Hidden)
                        {
                            dbGame.Hidden = game.Hidden;
                        }

                        if (game.Favorite)
                        {
                            dbGame.Favorite = game.Favorite;
                        }

                        db.Games.Update(dbGame);
                    }
                }

                dialogs.ShowMessage(resources.GetString("LOCImportCompleted"));
            }
            catch (Exception exc) when (!Environment.IsDebugBuild)
            {
                logger.Error(exc, "Failed to import Steam categories.");
                dialogs.ShowMessage(
                    resources.GetString("LOCSettingsSteamCatImportError"),
                    resources.GetString("LOCImportError"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #region ILibraryPlugin

        public override LibraryClient Client => new SteamClient();

        public override Guid Id => Guid.Parse("CB91DFC9-B977-43BF-8E70-55F46E410FAB");

        public override string Name => "Steam";

        public override string LibraryIcon => Steam.Icon;

        public override LibraryPluginCapabilities Capabilities { get; } = new LibraryPluginCapabilities
        {
            CanShutdownClient = true
        };

        public override void Dispose()
        {
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            LibrarySettings.IsFirstRunUse = firstRunSettings;
            return LibrarySettings;
        }

        public override UserControl GetSettingsView(bool firstRunView)
        {
            return new SteamLibrarySettingsView();
        }

        public override IEnumerable<GameInfo> GetGames()
        {
            var allGames = new List<GameInfo>();
            var installedGames = new Dictionary<string, GameInfo>();
            Exception importError = null;

            if (LibrarySettings.ImportInstalledGames)
            {
                try
                {
                    installedGames = GetInstalledGames();
                    logger.Debug($"Found {installedGames.Count} installed Steam games.");
                    allGames.AddRange(installedGames.Values.ToList());
                }
                catch (Exception e)
                {
                    logger.Error(e, "Failed to import installed Steam games.");
                    importError = e;
                }
            }

            if (LibrarySettings.ConnectAccount)
            {
                try
                {
                    var libraryGames = GetLibraryGames(LibrarySettings);
                    logger.Debug($"Found {libraryGames.Count} library Steam games.");
                    
                    if (!LibrarySettings.ImportUninstalledGames)
                    {
                        libraryGames = libraryGames.Where(lg => installedGames.ContainsKey(lg.GameId)).ToList();
                    }

                    //temp
                    var apiClient = new SteamApiClient();
                    //end temp
                    
                    foreach (var game in libraryGames)
                    {
                        if (installedGames.TryGetValue(game.GameId, out var installed))
                        {
                            // TODO check for games where the macOS/Linux version has a higher build than windows, so doesn't need to update (e.g. Dirt Rally)
                            installed.Playtime = game.Playtime;
                            installed.LastActivity = game.LastActivity;

                            if (!installed.Outdated) //If the manifest doesn't say its outdated we need to check
                            {
                                var productInfo = apiClient.GetProductInfo(uint.Parse(installed.GameId)).GetAwaiter()
                                    .GetResult();
                                var latestBuildId =
                                    uint.Parse(productInfo["depots"]["branches"][installed.Branch]["buildid"].Value);

                                // to prevent cases where version has not been set correctly
                                if (uint.TryParse(installed.Version, out var installedBuild))
                                {
                                    if (installedBuild < latestBuildId)
                                    {
                                        //Outdated
                                        installed.Outdated = true;
                                    }
                                }
                            }

                            if (installed.Outdated)
                            {
                                logger.Info($"Game: {game.Name} needs an update.");
                                PlayniteApi.Notifications.Add(new NotificationMessage(
                                    "updateAvailable-" + Guid.NewGuid(),
                                    string.Format("An update is available for {0} via Steam", installed.Name),
                                    NotificationType.Info,
                                    () => { Client.Open(); }
                                ));
                            }
                        }
                        else
                        {
                            allGames.Add(game);
                        }
                    }
                }
                catch (Exception e)
                {
                    logger.Error(e, "Failed to import linked account Steam games details.");
                    importError = e;
                }
            }

            if (importError != null)
            {
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    dbImportMessageId,
                    string.Format(PlayniteApi.Resources.GetString("LOCLibraryImportError"), Name) +
                    System.Environment.NewLine + importError.Message,
                    NotificationType.Error,
                    () => OpenSettingsView()));
            }
            else
            {
                PlayniteApi.Notifications.Remove(dbImportMessageId);
            }

            return allGames;
        }

        public override IGameController GetGameController(Game game)
        {
            return new SteamGameController(game, this);
        }

        public override LibraryMetadataProvider GetMetadataDownloader()
        {
            return new SteamMetadataProvider(this);
        }

        #endregion ILibraryPlugin
    }
}
