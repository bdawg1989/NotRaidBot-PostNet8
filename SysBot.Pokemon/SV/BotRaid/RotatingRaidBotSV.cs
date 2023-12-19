using Discord;
using Newtonsoft.Json;
using PKHeX.Core;
using RaidCrawler.Core.Structures;
using SysBot.Base;
using SysBot.Pokemon.SV.BotRaid.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static SysBot.Base.SwitchButton;
using static SysBot.Pokemon.RotatingRaidSettingsSV;
using static SysBot.Pokemon.SV.BotRaid.Blocks;

namespace SysBot.Pokemon.SV.BotRaid
{
    public class RotatingRaidBotSV : PokeRoutineExecutor9SV
    {
        private readonly PokeRaidHub<PK9> Hub;
        private readonly RotatingRaidSettingsSV Settings;
        private RemoteControlAccessList RaiderBanList => Settings.RaiderBanList;

        public RotatingRaidBotSV(PokeBotState cfg, PokeRaidHub<PK9> hub) : base(cfg)
        {
            Hub = hub;
            Settings = hub.Config.RotatingRaidSV;
        }

        public class PlayerInfo
        {
            public string OT { get; set; }
            public int RaidCount { get; set; }
        }

        private int LobbyError;
        private int RaidCount;
        private int WinCount;
        private int LossCount;
        private int SeedIndexToReplace = -1;
        public static GameProgress GameProgress;
        public static bool? currentSpawnsEnabled;
        public int StoryProgress;
        private int EventProgress;
        private int EmptyRaid = 0;
        private int LostRaid = 0;
        private bool firstRun = true;
        public static int RotationCount { get; set; }
        private ulong TodaySeed;
        private ulong OverworldOffset;
        private ulong ConnectedOffset;
        private ulong RaidBlockPointerP;
        private ulong RaidBlockPointerK;
        private ulong RaidBlockPointerB;
        private readonly ulong[] TeraNIDOffsets = new ulong[3];
        private string TeraRaidCode { get; set; } = string.Empty;
        private string BaseDescription = string.Empty;
        private readonly Dictionary<ulong, int> RaidTracker = new();
        private SAV9SV HostSAV = new();
        private DateTime StartTime = DateTime.Now;
        public static RaidContainer? container;
        public static bool IsKitakami = false;
        public static bool IsBlueberry = false;
        private DateTime TimeForRollBackCheck = DateTime.Now;
        private static bool hasSwapped = false;
        private uint originalAreaId;
        private uint originalDenId;
        private bool originalIdsSet = false;
        private uint areaIdIndex0;
        private uint denIdIndex0;
        private uint areaIdIndex1;
        private uint denIdIndex1;
        private bool indicesInitialized = false;

        public override async Task MainLoop(CancellationToken token)
        {

            if (Settings.RaidSettings.GenerateRaidsFromFile)
            {
                GenerateSeedsFromFile();
                Log("Done.");
                Settings.RaidSettings.GenerateRaidsFromFile = false;
            }

            if (Settings.MiscSettings.ConfigureRolloverCorrection)
            {
                await RolloverCorrectionSV(token).ConfigureAwait(false);
                return;
            }

            if (Settings.ActiveRaids.Count < 1)
            {
                Log("ActiveRaids cannot be 0. Please setup your parameters for the raid(s) you are hosting.");
                return;
            }

            if (Settings.RaidSettings.TimeToWait is < 0 or > 180)
            {
                Log("Time to wait must be between 0 and 180 seconds.");
                return;
            }

            try
            {
                Log("Identifying trainer data of the host console.");
                HostSAV = await IdentifyTrainer(token).ConfigureAwait(false);
                await InitializeHardware(Settings, token).ConfigureAwait(false);
                Log("Starting main RotatingRaidBot loop.");
                await InnerLoop(token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log(e.Message);
            }
            finally
            {
                SaveSeeds();
            }
            Log($"Ending {nameof(RotatingRaidBotSV)} loop.");
            await HardStop().ConfigureAwait(false);
        }

        public class PlayerDataStorage
        {
            private readonly string filePath;

            public PlayerDataStorage(string baseDirectory)
            {
                var directoryPath = Path.Combine(baseDirectory, "raidfilessv");
                Directory.CreateDirectory(directoryPath);
                filePath = Path.Combine(directoryPath, "player_data.json");

                if (!File.Exists(filePath))
                    File.WriteAllText(filePath, "{}"); // Create a new JSON file if it does not exist.
            }

            public Dictionary<ulong, PlayerInfo> LoadPlayerData()
            {
                string json = File.ReadAllText(filePath);
                return JsonConvert.DeserializeObject<Dictionary<ulong, PlayerInfo>>(json) ?? new Dictionary<ulong, PlayerInfo>();
            }

            public void SavePlayerData(Dictionary<ulong, PlayerInfo> data)
            {
                string json = JsonConvert.SerializeObject(data, Formatting.Indented);
                File.WriteAllText(filePath, json);
            }
        }

        private void GenerateSeedsFromFile()
        {
            var folder = "raidfilessv";
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            var prevrotationpath = "raidsv.txt";
            var rotationpath = "raidfilessv\\raidsv.txt";
            if (File.Exists(prevrotationpath))
                File.Move(prevrotationpath, rotationpath);
            if (!File.Exists(rotationpath))
            {
                File.WriteAllText(rotationpath, "000091EC-Kricketune-3-6,0000717F-Seviper-3-6");
                Log("Creating a default raidsv.txt file, skipping generation as file is empty.");
                return;
            }

            if (!File.Exists(rotationpath))
                Log("raidsv.txt not present, skipping parameter generation.");

            BaseDescription = string.Empty;
            var prevpath = "bodyparam.txt";
            var filepath = "raidfilessv\\bodyparam.txt";
            if (File.Exists(prevpath))
                File.Move(prevpath, filepath);
            if (File.Exists(filepath))
                BaseDescription = File.ReadAllText(filepath);

            var data = string.Empty;
            var prevpk = "pkparam.txt";
            var pkpath = "raidfilessv\\pkparam.txt";
            if (File.Exists(prevpk))
                File.Move(prevpk, pkpath);
            if (File.Exists(pkpath))
                data = File.ReadAllText(pkpath);

            DirectorySearch(rotationpath, data);
        }

        private void SaveSeeds()
        {
            // Exit the function if saving seeds to file is not enabled
            if (!Settings.RaidSettings.SaveSeedsToFile)
                return;

            // Filter out raids that don't need to be saved
            var raidsToSave = Settings.ActiveRaids.Where(raid => !raid.AddedByRACommand).ToList();

            // Exit the function if there are no raids to save
            if (!raidsToSave.Any())
                return;

            // Define directory and file paths
            var directoryPath = "raidfilessv";
            var fileName = "savedSeeds.txt";
            var savePath = Path.Combine(directoryPath, fileName);

            // Create directory if it doesn't exist
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            // Initialize StringBuilder to build the save string
            StringBuilder sb = new StringBuilder();

            // Loop through each raid to be saved
            foreach (var raid in raidsToSave)
            {
                // Increment the StoryProgressLevel by 1 before saving
                int incrementedStoryProgressLevel = raid.StoryProgressLevel + 1;

                // Build the string to save, including the incremented StoryProgressLevel
                sb.Append($"{raid.Seed}-{raid.Species}-{raid.DifficultyLevel}-{incrementedStoryProgressLevel},");
            }

            // Remove the trailing comma at the end
            if (sb.Length > 0)
                sb.Length--;

            // Write the built string to the file
            File.WriteAllText(savePath, sb.ToString());
        }

        private void DirectorySearch(string sDir, string data)
        {
            // Clear the active raids before populating it
            Settings.ActiveRaids.Clear();

            // Read the entire content from the file into a string
            string contents = File.ReadAllText(sDir);

            // Split the string based on commas to get each raid entry
            string[] moninfo = contents.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            // Iterate over each raid entry
            for (int i = 0; i < moninfo.Length; i++)
            {
                // Split the entry based on dashes to get individual pieces of information
                var div = moninfo[i].Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries);

                // Check if the split result has exactly 4 parts
                if (div.Length != 4)
                {
                    Log($"Error processing entry: {moninfo[i]}. Expected 4 parts but found {div.Length}. Skipping this entry.");
                    continue; // Skip processing this entry and move to the next one
                }

                // Extracting seed, title, and difficulty level
                var monseed = div[0];
                var montitle = div[1];

                if (!int.TryParse(div[2], out int difficultyLevel))
                {
                    Log($"Unable to parse difficulty level for entry: {moninfo[i]}");
                    continue;
                }

                // Extract and convert the StoryProgressLevel
                if (!int.TryParse(div[3], out int storyProgressLevelFromSeed))
                {
                    Log($"Unable to parse StoryProgressLevel for entry: {moninfo[i]}");
                    continue;
                }

                int convertedStoryProgressLevel = storyProgressLevelFromSeed - 1; // Converting based on given conditions

                // Determine the TeraCrystalType based on the difficulty level
                TeraCrystalType type = difficultyLevel switch
                {
                    6 => TeraCrystalType.Black,
                    7 => TeraCrystalType.Might,
                    _ => TeraCrystalType.Base,
                };

                // Create a new RotatingRaidParameters object and populate its properties
                RotatingRaidParameters param = new()
                {
                    Seed = monseed,
                    Title = montitle,
                    Species = RaidExtensions<PK9>.EnumParse<Species>(montitle),
                    CrystalType = type,
                    PartyPK = new[] { data },
                    DifficultyLevel = difficultyLevel,  // Set the DifficultyLevel
                    StoryProgressLevel = convertedStoryProgressLevel  // Set the converted StoryProgressLevel
                };

                // Add the RotatingRaidParameters object to the ActiveRaids list
                Settings.ActiveRaids.Add(param);

                // Log the raid parameter generation
                Log($"Parameters generated from text file for {montitle}.");
            }
        }

        private async Task InnerLoop(CancellationToken token)
        {
            bool partyReady;
            List<(ulong, RaidMyStatus)> lobbyTrainers;
            StartTime = DateTime.Now;
            var dayRoll = 0;
            RotationCount = 0;
            var raidsHosted = 0;
            while (!token.IsCancellationRequested)
            {

                // Initialize offsets at the start of the routine and cache them.
                await InitializeSessionOffsets(token).ConfigureAwait(false);
                if (RaidCount == 0)
                {
                    TodaySeed = BitConverter.ToUInt64(await SwitchConnection.ReadBytesAbsoluteAsync(RaidBlockPointerP, 8, token).ConfigureAwait(false), 0);
                    Log($"Today Seed: {TodaySeed:X8}");
                    Log($"Preparing to store index for {Settings.ActiveRaids[RotationCount].Species}");
                    await ReadRaids(true, token).ConfigureAwait(false);
                }

                Log($"Preparing parameter for {Settings.ActiveRaids[RotationCount].Species}");
                await ReadRaids(false, token).ConfigureAwait(false);

                var currentSeed = BitConverter.ToUInt64(await SwitchConnection.ReadBytesAbsoluteAsync(RaidBlockPointerP, 8, token).ConfigureAwait(false), 0);
                if (TodaySeed != currentSeed || LobbyError >= 2)
                {
                    var msg = "";
                    if (TodaySeed != currentSeed)
                    {
                        Log($"Current Today Seed {currentSeed:X8} does not match Starting Today Seed: {TodaySeed:X8}.\nAttempting to override Today Seed...");
                        TodaySeed = currentSeed;  // Update the TodaySeed to the currentSeed
                        await OverrideTodaySeed(token).ConfigureAwait(false); // Override the Today Seed in the game to match the currentSeed
                        Log("Today Seed has been overridden with the current seed.");
                    }

                    if (LobbyError >= 2)
                    {
                        msg = $"Failed to create a lobby {LobbyError} times.\n ";
                        dayRoll++;
                    }

                    if (dayRoll != 0 && SeedIndexToReplace != -1 && RaidCount != 0)
                    {
                        Log(msg + "Raid Lost initiating recovery sequence.");
                        bool denFound = false;
                        while (!denFound)
                        {
                            await Click(B, 0_500, token).ConfigureAwait(false);
                            await Click(HOME, 3_500, token).ConfigureAwait(false);
                            Log("Closed out of the game!");

                            await RolloverCorrectionSV(token).ConfigureAwait(false);
                            await Click(A, 1_500, token).ConfigureAwait(false);
                            Log("Back in the game!");

                            while (!await IsConnectedOnline(ConnectedOffset, token).ConfigureAwait(false))
                            {
                                Log("Connecting...");
                                if (!await ConnectToOnline(Hub.Config, token).ConfigureAwait(false))
                                    continue;

                                await RecoverToOverworld(token).ConfigureAwait(false);
                            }

                            await RecoverToOverworld(token).ConfigureAwait(false);

                            // Check if there's a lobby.
                            if (!await GetLobbyReady(true, token).ConfigureAwait(false))
                            {
                                continue;
                            }
                            else
                            {
                                Log("Den Found, continuing routine!");
                                TodaySeed = BitConverter.ToUInt64(await SwitchConnection.ReadBytesAbsoluteAsync(RaidBlockPointerP, 8, token).ConfigureAwait(false), 0);
                                LobbyError = 0;
                                denFound = true;
                                firstRun = true;
                                hasSwapped = false;
                                originalIdsSet = false;
                                indicesInitialized = false;
                                await Task.Delay(5_000, token).ConfigureAwait(false);
                                await Click(B, 1_000, token).ConfigureAwait(false);
                                await Task.Delay(3_000, token).ConfigureAwait(false);
                                await Click(A, 1_000, token).ConfigureAwait(false);
                                await Task.Delay(5_000, token).ConfigureAwait(false);
                                await Click(B, 1_000, token).ConfigureAwait(false);
                                await Click(B, 1_000, token).ConfigureAwait(false);
                                await Task.Delay(1_000, token).ConfigureAwait(false);

                            }
                        };
                        await Task.Delay(0_050, token).ConfigureAwait(false);
                        if (denFound)
                        {
                            await SVSaveGameOverworld(token).ConfigureAwait(false);
                            await Task.Delay(0_500, token).ConfigureAwait(false);
                            await Click(B, 1_000, token).ConfigureAwait(false);
                            continue;
                        }
                    }
                    Log(msg);
                    await CloseGame(Hub.Config, token).ConfigureAwait(false);
                    await RolloverCorrectionSV(token).ConfigureAwait(false);
                    await StartGameRaid(Hub.Config, token).ConfigureAwait(false);
                    dayRoll++;
                    continue;
                }

                // Clear NIDs.
                await SwitchConnection.WriteBytesAbsoluteAsync(new byte[32], TeraNIDOffsets[0], token).ConfigureAwait(false);

                // Connect online and enter den.
                if (!await PrepareForRaid(token).ConfigureAwait(false))
                {
                    Log("Failed to prepare the raid, rebooting the game.");
                    await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
                    continue;
                }

                // Wait until we're in lobby.
                if (!await GetLobbyReady(false, token).ConfigureAwait(false))
                    continue;

                if (Settings.ActiveRaids[RotationCount].AddedByRACommand)
                {
                    var user = Settings.ActiveRaids[RotationCount].User;

                    // Determine if the raid is a "Free For All"
                    bool isFreeForAll = !Settings.ActiveRaids[RotationCount].IsCoded || EmptyRaid >= Settings.LobbyOptions.EmptyRaidLimit;

                    if (user != null && !isFreeForAll)
                    {
                        try
                        {
                            // Only get and send the raid code if it's not a "Free For All"
                            var code = await GetRaidCode(token).ConfigureAwait(false);
                            await user.SendMessageAsync($"Your Raid Code is **{code}**").ConfigureAwait(false);
                        }
                        catch (Discord.Net.HttpException ex)
                        {
                            // Handle exception (e.g., log the error or send a message to a logging channel)
                            Log($"Failed to send DM to {user.Username}. They might have DMs turned off. Exception: {ex.Message}");
                        }
                    }
                }

                // Read trainers until someone joins.
                (partyReady, lobbyTrainers) = await ReadTrainers(token).ConfigureAwait(false);
                if (!partyReady)
                {
                    if (LostRaid >= Settings.LobbyOptions.SkipRaidLimit && Settings.LobbyOptions.LobbyMethod == LobbyMethodOptions.SkipRaid)
                    {
                        await SkipRaidOnLosses(token).ConfigureAwait(false);
                        EmptyRaid = 0;
                        continue;
                    }

                    // Should add overworld recovery with a game restart fallback.
                    await RegroupFromBannedUser(token).ConfigureAwait(false);

                    if (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                    {
                        Log("Something went wrong, attempting to recover.");
                        await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
                        continue;
                    }

                    // Clear trainer OTs.
                    Log("Clearing stored OTs");
                    for (int i = 0; i < 3; i++)
                    {
                        List<long> ptr = new(Offsets.Trader2MyStatusPointer);
                        ptr[2] += i * 0x30;
                        await SwitchConnection.PointerPoke(new byte[16], ptr, token).ConfigureAwait(false);
                    }
                    continue;
                }
                await CompleteRaid(token).ConfigureAwait(false);
                raidsHosted++;
                if (raidsHosted == Settings.RaidSettings.TotalRaidsToHost && Settings.RaidSettings.TotalRaidsToHost > 0)
                    break;
            }
            if (Settings.RaidSettings.TotalRaidsToHost > 0 && raidsHosted != 0)
                Log("Total raids to host has been met.");
        }

        public override async Task HardStop()
        {
            try
            {
                Directory.Delete("cache", true);
            }
            catch (Exception)
            {
                //dgaf about cache not existing
            }

            // Remove all Mystery Shiny Raids and other raids added by RA command
            Settings.ActiveRaids.RemoveAll(p => p.AddedByRACommand);
            Settings.ActiveRaids.RemoveAll(p => p.Title == "Mystery Shiny Raid");
            await CleanExit(CancellationToken.None).ConfigureAwait(false);

        }

        private async Task LocateSeedIndex(CancellationToken token)
        {
            // Skip updating SeedIndexToReplace for Might or Distribution Raids
            if (Settings.ActiveRaids[RotationCount].CrystalType == TeraCrystalType.Might ||
                Settings.ActiveRaids[RotationCount].CrystalType == TeraCrystalType.Distribution)
            {
                // Skipping SeedIndexToReplace update for Might or Distribution Raid
                return;
            }

            var data = await SwitchConnection.ReadBytesAbsoluteAsync(RaidBlockPointerP, 2304, token).ConfigureAwait(false);
            for (int i = 0; i < 69; i++)  // Zero-based index for Paldea Raids
            {
                var seed = BitConverter.ToUInt32(data.AsSpan(i * 32, 4));
                if (seed == 0)
                {
                    SeedIndexToReplace = i;  // Zero-based index
                    Log($"Raid Den Located at {i + 1:00} in Paldea map");
                    return;
                }
            }

            data = await SwitchConnection.ReadBytesAbsoluteAsync(RaidBlockPointerK, (int)RaidBlock.SIZE_KITAKAMI, token).ConfigureAwait(false);
            for (int i = 0; i < 25; i++)  // Zero-based index for Kitakami Raids
            {
                var seed = BitConverter.ToUInt32(data.AsSpan(i * 32, 4));
                if (seed == 0)
                {
                    SeedIndexToReplace = i + 69;  // Adjust for Kitakami starting index
                    Log($"Raid Den Located at {i + 70:00} in Kitakami map");
                    IsKitakami = true;
                    return;
                }
            }

            data = await SwitchConnection.ReadBytesAbsoluteAsync(RaidBlockPointerB, (int)RaidBlock.SIZE_BLUEBERRY, token).ConfigureAwait(false);
            for (int i = 0; i < 23; i++)  // Zero-based index for Blueberry Raids
            {
                var seed = BitConverter.ToUInt32(data.AsSpan(i * 32, 4));
                if (seed == 0)
                {
                    SeedIndexToReplace = i + 93;  // Adjust for Blueberry starting index
                    Log($"Raid Den Located at {i + 94:00} in Blueberry map");
                    IsBlueberry = true;
                    return;
                }
            }

            Log($"Index not located.");
        }

        private async Task CompleteRaid(CancellationToken token)
        {
            var trainers = new List<(ulong, RaidMyStatus)>();

            // Ensure connection to lobby and log status
            if (!await CheckIfConnectedToLobbyAndLog(token))
            {
                await ReOpenGame(Hub.Config, token);
                return;
            }

            // Ensure in raid
            if (!await EnsureInRaid(token))
            {
                await ReOpenGame(Hub.Config, token);
                return;
            }

            // Use the ScreenshotTiming setting for the delay before taking a screenshot in Raid
            var screenshotDelay = (int)Settings.EmbedToggles.ScreenshotTiming;

            // Use the delay in milliseconds as needed
            await Task.Delay(screenshotDelay, token).ConfigureAwait(false);

            var lobbyTrainersFinal = new List<(ulong, RaidMyStatus)>();
            if (!await UpdateLobbyTrainersFinal(lobbyTrainersFinal, trainers, token))
            {
                await ReOpenGame(Hub.Config, token);
                return;
            }

            // Handle duplicates and embeds first
            if (!await HandleDuplicatesAndEmbeds(lobbyTrainersFinal, token))
            {
                await ReOpenGame(Hub.Config, token);
                return;
            }

            // Delay to start ProcessBattleActions
            await Task.Delay(10_000, token).ConfigureAwait(false);

            // Process battle actions
            if (!await ProcessBattleActions(token))
            {
                await ReOpenGame(Hub.Config, token);
                return;
            }

            // Handle end of raid actions
            bool ready = await HandleEndOfRaidActions(token);
            if (!ready)
            {
                await ReOpenGame(Hub.Config, token);
                return;
            }

            // Finalize raid completion
            await FinalizeRaidCompletion(trainers, ready, token);
        }

        private async Task<bool> CheckIfConnectedToLobbyAndLog(CancellationToken token)
        {
            if (await IsConnectedToLobby(token).ConfigureAwait(false))
            {
                Log("Preparing for battle!");
                return true;
            }
            return false;
        }

        private async Task<bool> EnsureInRaid(CancellationToken linkedToken)
        {
            var startTime = DateTime.Now;

            while (!await IsInRaid(linkedToken).ConfigureAwait(false))
            {
                if (linkedToken.IsCancellationRequested || (DateTime.Now - startTime).TotalMinutes > 5) // 5-minute timeout
                {
                    Log("Timeout reached or cancellation requested, resetting to recover.");
                    await ReOpenGame(Hub.Config, linkedToken).ConfigureAwait(false);
                    return false;
                }
                await Click(A, 1_000, linkedToken).ConfigureAwait(false);
            }
            return true;
        }

        public async Task<bool> UpdateLobbyTrainersFinal(List<(ulong, RaidMyStatus)> lobbyTrainersFinal, List<(ulong, RaidMyStatus)> trainers, CancellationToken token)
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var storage = new PlayerDataStorage(baseDirectory);
            var playerData = storage.LoadPlayerData();

            // Clear NIDs to refresh player check.
            await SwitchConnection.WriteBytesAbsoluteAsync(new byte[32], TeraNIDOffsets[0], token).ConfigureAwait(false);
            await Task.Delay(5_000, token).ConfigureAwait(false);

            // Loop through trainers again in case someone disconnected.
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    var nidOfs = TeraNIDOffsets[i];
                    var data = await SwitchConnection.ReadBytesAbsoluteAsync(nidOfs, 8, token).ConfigureAwait(false);
                    var nid = BitConverter.ToUInt64(data, 0);

                    if (nid == 0)
                        continue;

                    List<long> ptr = new(Offsets.Trader2MyStatusPointer);
                    ptr[2] += i * 0x30;
                    var trainer = await GetTradePartnerMyStatus(ptr, token).ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(trainer.OT) || HostSAV.OT == trainer.OT)
                        continue;

                    lobbyTrainersFinal.Add((nid, trainer));

                    if (!playerData.TryGetValue(nid, out var info))
                    {
                        // New player
                        playerData[nid] = new PlayerInfo { OT = trainer.OT, RaidCount = 1 };
                        Log($"New Player: {trainer.OT} | TID: {trainer.DisplayTID} | NID: {nid}.");
                    }
                    else
                    {
                        // Returning player
                        info.RaidCount++;
                        playerData[nid] = info; // Update the info back to the dictionary.
                        Log($"Returning Player: {trainer.OT} | TID: {trainer.DisplayTID} | NID: {nid} | Raids: {info.RaidCount}");
                    }
                }
                catch (IndexOutOfRangeException ex)
                {
                    Log($"Index out of range exception caught: {ex.Message}");
                    return false;
                }
                catch (Exception ex)
                {
                    Log($"An unknown error occurred: {ex.Message}");
                    return false;
                }
            }

            // Save player data after processing all players.
            storage.SavePlayerData(playerData);
            return true;
        }

        private async Task<bool> HandleDuplicatesAndEmbeds(List<(ulong, RaidMyStatus)> lobbyTrainersFinal, CancellationToken token)
        {
            var nidDupe = lobbyTrainersFinal.Select(x => x.Item1).ToList();
            var dupe = lobbyTrainersFinal.Count > 1 && nidDupe.Distinct().Count() == 1;
            if (dupe)
            {
                // We read bad data, reset game to end early and recover.
                var msg = "Oops! Something went wrong, resetting to recover.";
                bool success = false;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        await Task.Delay(20_000, token).ConfigureAwait(false);
                        await EnqueueEmbed(null, "", false, false, false, false, token).ConfigureAwait(false);
                        success = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"Attempt {attempt} failed with error: {ex.Message}");
                        if (attempt == 3)
                        {
                            Log("All attempts failed. Continuing without sending embed.");
                        }
                    }
                }

                if (!success)
                {
                    await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
                    return false;
                }
            }

            var names = lobbyTrainersFinal.Select(x => x.Item2.OT).ToList();
            bool hatTrick = lobbyTrainersFinal.Count == 3 && names.Distinct().Count() == 1;

            bool embedSuccess = false;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await EnqueueEmbed(names, "", hatTrick, false, false, true, token).ConfigureAwait(false);
                    embedSuccess = true;
                    break;
                }
                catch (Exception ex)
                {
                    Log($"Attempt {attempt} failed with error: {ex.Message}");
                    if (attempt == 3)
                    {
                        Log("All attempts failed. Continuing without sending embed.");
                    }
                }
            }

            return embedSuccess;
        }

        private async Task<bool> ProcessBattleActions(CancellationToken token)
        {
            int nextUpdateMinute = 2;
            DateTime battleStartTime = DateTime.Now;
            bool hasPerformedAction1 = false;
            bool timedOut = false;
            bool hasPressedHome = false;

            while (await IsConnectedToLobby(token).ConfigureAwait(false))
            {
                TimeSpan timeInBattle = DateTime.Now - battleStartTime;

                // Check for battle timeout
                if (timeInBattle.TotalMinutes >= 15)
                {
                    Log("Battle timed out after 15 minutes. Even Netflix asked if I was still watching...");
                    timedOut = true;
                    break;
                }

                // Handle the first action with a delay
                if (!hasPerformedAction1)
                {
                    int action1DelayInSeconds = Settings.ActiveRaids[RotationCount].Action1Delay;
                    var action1Name = Settings.ActiveRaids[RotationCount].Action1;
                    int action1DelayInMilliseconds = action1DelayInSeconds * 1000;
                    Log($"Waiting {action1DelayInSeconds} seconds. No rush, we're chilling.");
                    await Task.Delay(action1DelayInMilliseconds, token).ConfigureAwait(false);
                    await MyActionMethod(token).ConfigureAwait(false);
                    Log($"{action1Name} done. Wasn't that fun?");
                    hasPerformedAction1 = true;
                }
                else
                {
                    // Execute raid actions based on configuration
                    switch (Settings.LobbyOptions.Action)
                    {
                        case RaidAction.AFK:
                            await Task.Delay(3_000, token).ConfigureAwait(false);
                            break;

                        case RaidAction.MashA:
                            if (await IsConnectedToLobby(token).ConfigureAwait(false))
                            {
                                int mashADelayInMilliseconds = (int)(Settings.LobbyOptions.MashADelay * 1000);
                                await Click(A, mashADelayInMilliseconds, token).ConfigureAwait(false);
                            }
                            break;
                    }
                }

                // Periodic battle status log at 2-minute intervals
                if (timeInBattle.TotalMinutes >= nextUpdateMinute)
                {
                    Log($"{nextUpdateMinute} minutes have passed. We are still in battle...");
                    nextUpdateMinute += 2; // Update the time for the next status update.
                }
                // Check if the battle has been ongoing for 6 minutes
                if (timeInBattle.TotalMinutes >= 6 && !hasPressedHome)
                {
                    // Hit Home button twice in case we are stuck
                    await Click(HOME, 0_500, token).ConfigureAwait(false);
                    await Click(HOME, 0_500, token).ConfigureAwait(false);
                    hasPressedHome = true;
                }
                // Make sure to wait some time before the next iteration to prevent a tight loop
                await Task.Delay(1000, token); // Wait for a second before checking again
            }

            return !timedOut;
        }

        private async Task<bool> HandleEndOfRaidActions(CancellationToken token)
        {
            LobbyFiltersCategory settings = new LobbyFiltersCategory();

            Log("Raid lobby disbanded!");
            await Task.Delay(1_500 + settings.ExtraTimeLobbyDisband, token).ConfigureAwait(false);
            await Click(B, 0_500, token).ConfigureAwait(false);
            await Click(B, 0_500, token).ConfigureAwait(false);
            await Click(DDOWN, 0_500, token).ConfigureAwait(false);
            await Click(A, 0_500, token).ConfigureAwait(false);
            bool ready = true;

            if (Settings.LobbyOptions.LobbyMethod == LobbyMethodOptions.SkipRaid)
            {
                Log($"Lost/Empty Lobbies: {LostRaid}/{Settings.LobbyOptions.SkipRaidLimit}");

                if (LostRaid >= Settings.LobbyOptions.SkipRaidLimit)
                {
                    Log($"We had {Settings.LobbyOptions.SkipRaidLimit} lost/empty raids.. Moving on!");
                    await SanitizeRotationCount(token).ConfigureAwait(false);
                    await EnqueueEmbed(null, "", false, false, true, false, token).ConfigureAwait(false);
                    ready = true;
                }
            }

            return ready;
        }

        private async Task FinalizeRaidCompletion(List<(ulong, RaidMyStatus)> trainers, bool ready, CancellationToken token)
        {
            Log("Returning to overworld...");
            await Task.Delay(3_500, token).ConfigureAwait(false);
            while (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                await Click(A, 1_000, token).ConfigureAwait(false);

            await CountRaids(trainers, token).ConfigureAwait(false);
            await LocateSeedIndex(token).ConfigureAwait(false);

            // Update RotationCount after locating seed index
            if (Settings.ActiveRaids.Count > 1)
            {
                await SanitizeRotationCount(token).ConfigureAwait(false);
            }
            await EnqueueEmbed(null, "", false, false, true, false, token).ConfigureAwait(false);
            await Task.Delay(0_500, token).ConfigureAwait(false);
            await CloseGame(Hub.Config, token).ConfigureAwait(false);

            if (ready)
                await StartGameRaid(Hub.Config, token).ConfigureAwait(false);
            else
            {
                if (Settings.ActiveRaids.Count > 1)
                {
                    if (RotationCount < Settings.ActiveRaids.Count && Settings.ActiveRaids.Count > 1)
                        RotationCount++;
                    if (RotationCount >= Settings.ActiveRaids.Count && Settings.ActiveRaids.Count > 1)
                    {
                        RotationCount = 0;
                        Log($"Resetting Rotation Count to {RotationCount}");
                    }
                    Log($"Moving on to next rotation for {Settings.ActiveRaids[RotationCount].Species}.");
                    await StartGameRaid(Hub.Config, token).ConfigureAwait(false);
                }
                else
                    await StartGame(Hub.Config, token).ConfigureAwait(false);
            }

            if (Settings.RaidSettings.KeepDaySeed)
                await OverrideTodaySeed(token).ConfigureAwait(false);
        }

        public async Task MyActionMethod(CancellationToken token)
        {
            // Let's rock 'n roll with these moves!
            switch (Settings.ActiveRaids[RotationCount].Action1)
            {
                case Action1Type.GoAllOut:
                    await Click(DDOWN, 0_500, token).ConfigureAwait(false);
                    await Click(A, 0_500, token).ConfigureAwait(false);
                    for (int i = 0; i < 3; i++)
                    {
                        await Click(A, 0_500, token).ConfigureAwait(false);
                    }
                    break;

                case Action1Type.HangTough:
                case Action1Type.HealUp:
                    await Click(DDOWN, 0_500, token).ConfigureAwait(false);
                    await Click(A, 0_500, token).ConfigureAwait(false);
                    int ddownTimes = Settings.ActiveRaids[RotationCount].Action1 == Action1Type.HangTough ? 1 : 2;
                    for (int i = 0; i < ddownTimes; i++)
                    {
                        await Click(DDOWN, 0_500, token).ConfigureAwait(false);
                    }
                    for (int i = 0; i < 3; i++)
                    {
                        await Click(A, 0_500, token).ConfigureAwait(false);
                    }
                    break;

                case Action1Type.Move1:
                    await Click(A, 0_500, token).ConfigureAwait(false);
                    for (int i = 0; i < 3; i++)
                    {
                        await Click(A, 0_500, token).ConfigureAwait(false);
                    }
                    break;

                case Action1Type.Move2:
                case Action1Type.Move3:
                case Action1Type.Move4:
                    await Click(A, 0_500, token).ConfigureAwait(false);
                    int moveDdownTimes = Settings.ActiveRaids[RotationCount].Action1 == Action1Type.Move2 ? 1 : Settings.ActiveRaids[RotationCount].Action1 == Action1Type.Move3 ? 2 : 3;
                    for (int i = 0; i < moveDdownTimes; i++)
                    {
                        await Click(DDOWN, 0_500, token).ConfigureAwait(false);
                    }
                    for (int i = 0; i < 3; i++)
                    {
                        await Click(A, 0_500, token).ConfigureAwait(false);
                    }
                    break;

                default:
                    Console.WriteLine("Unknown action, what's the move?");
                    throw new InvalidOperationException("Unknown action type!");
            }
        }

        private async Task CountRaids(List<(ulong, RaidMyStatus)>? trainers, CancellationToken token)
        {
            int countP = 0;
            int countK = 0;
            int countB = 0;  // Count for Blueberry raids

            // Read data from RaidBlockPointerP (Paldea Raids)
            var dataP = await SwitchConnection.ReadBytesAbsoluteAsync(RaidBlockPointerP, 2304, token).ConfigureAwait(false);
            for (int i = 0; i < 69; i++)  // 69 Paldea raids
            {
                var seed = BitConverter.ToUInt32(dataP.AsSpan(i * 32, 4));
                if (seed != 0)
                    countP++;
            }

            // Read data from RaidBlockPointerK (Kitakami Raids)
            var dataK = await SwitchConnection.ReadBytesAbsoluteAsync(RaidBlockPointerK, 25 * 32, token).ConfigureAwait(false);
            for (int i = 0; i < 25; i++)  // 25 Kitakami raids
            {
                var seed = BitConverter.ToUInt32(dataK.AsSpan(i * 32, 4));
                if (seed != 0)
                    countK++;
            }

            // Read data from RaidBlockPointerB (Blueberry Raids)
            var dataB = await SwitchConnection.ReadBytesAbsoluteAsync(RaidBlockPointerB, 23 * 32, token).ConfigureAwait(false);
            for (int i = 0; i < 23; i++)  // 23 Blueberry raids
            {
                var seed = BitConverter.ToUInt32(dataB.AsSpan(i * 32, 4));
                if (seed != 0)
                    countB++;
            }

            if (trainers is not null)
            {
                Log("Back in the overworld, checking if we won or lost.");

                // Adjust the win/loss condition based on the raid counts
                if ((countP < 69 && countK == 25 && countB == 23) ||
                    (countP == 69 && countK < 25 && countB == 23) ||
                    (countP == 69 && countK == 25 && countB < 23))
                {
                    Log("Yay! We defeated the raid!");
                    WinCount++;
                }
                else
                {
                    Log("Dang, we lost the raid.");
                    LossCount++;
                }
            }
        }

        private async Task OverrideTodaySeed(CancellationToken token)
        {
            Log("Attempting to override Today Seed...");

            var todayoverride = BitConverter.GetBytes(TodaySeed);
            List<long> ptr = new(Offsets.RaidBlockPointerP);
            ptr[3] += 0x8;
            await SwitchConnection.PointerPoke(todayoverride, ptr, token).ConfigureAwait(false);

            Log("Today Seed override complete.");
        }

        private async Task OverrideSeedIndex(int index, CancellationToken token)
        {
            if (index == -1)
                return;

            List<long> ptr = DeterminePointer(index); // Using DeterminePointer

            var crystalType = Settings.ActiveRaids[RotationCount].CrystalType;
            var seed = uint.Parse(Settings.ActiveRaids[RotationCount].Seed, NumberStyles.AllowHexSpecifier);

            if (crystalType == TeraCrystalType.Might || crystalType == TeraCrystalType.Distribution)
            {
                Log(crystalType == TeraCrystalType.Might ? "Preparing 7 Star Event Raid..." : "Preparing Distribution Raid...");

                // Overriding the seed
                byte[] seedBytes = BitConverter.GetBytes(seed);
                await SwitchConnection.PointerPoke(seedBytes, ptr, token).ConfigureAwait(false);

                // Adjust the pointer for the crystal type depending on the raid type
                var crystalPtr = new List<long>(ptr);
                crystalPtr[3] += 0x08;

                byte[] crystalBytes = BitConverter.GetBytes((int)crystalType);
                await SwitchConnection.PointerPoke(crystalBytes, crystalPtr, token).ConfigureAwait(false);
                await Task.Delay(1_500, token).ConfigureAwait(false);

                string raidType = crystalType == TeraCrystalType.Might ? "Might" : "Distribution";
                await SwapRaidLocationsAsync(index, raidType, token).ConfigureAwait(false);
                await Task.Delay(1_500, token).ConfigureAwait(false);
                await SyncSeedToIndexZero(index, raidType, token).ConfigureAwait(false);
            }
            else
            {
                // Check if crystal type is Black or Base and if swapping has already been done
                if ((crystalType == TeraCrystalType.Black || crystalType == TeraCrystalType.Base) && hasSwapped)
                {
                    //  Log($"CrystalType is {crystalType}, proceeding with re-swapping Area ID and Den ID.");
                    string raidType = crystalType == TeraCrystalType.Black ? "Black" : "Base";
                    await SwapRaidLocationsAsync(index, raidType, token).ConfigureAwait(false);
                    await Task.Delay(1_500, token).ConfigureAwait(false);
                }

                // Overriding the seed
                byte[] inj = BitConverter.GetBytes(seed);
                var currseed = await SwitchConnection.PointerPeek(4, ptr, token).ConfigureAwait(false);

                // Reverse the byte array of the current seed for logging purposes if necessary
                byte[] currSeedForLogging = (byte[])currseed.Clone();
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(currSeedForLogging);
                }

                // Reverse the byte array of the new seed for logging purposes if necessary
                byte[] injForLogging = (byte[])inj.Clone();
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(injForLogging);
                }

                // Convert byte arrays to hexadecimal strings for logging
                string currSeedHex = BitConverter.ToString(currSeedForLogging).Replace("-", "");
                string newSeedHex = BitConverter.ToString(injForLogging).Replace("-", "");

                Log($"Replacing {currSeedHex} with {newSeedHex}.");
                await SwitchConnection.PointerPoke(inj, ptr, token).ConfigureAwait(false);

                // Overriding the crystal type
                var ptr2 = new List<long>(ptr);
                ptr2[3] += 0x08;
                var crystal = BitConverter.GetBytes((int)crystalType);
                var currcrystal = await SwitchConnection.PointerPeek(1, ptr2, token).ConfigureAwait(false);
                if (currcrystal != crystal)
                    await SwitchConnection.PointerPoke(crystal, ptr2, token).ConfigureAwait(false);
            }
        }

        private void CreateAndAddRandomShinyRaidAsRequested()
        {
            // Generate a random shiny seed
            uint randomSeed = GenerateRandomShinySeed();
            Random random = new Random();

            // Get the settings object
            var mysteryRaidsSettings = Settings.RaidSettings.MysteryRaidsSettings;

            // Check if any Mystery Raid setting is enabled
            if (!(mysteryRaidsSettings.Unlocked3StarSettings.Enabled || mysteryRaidsSettings.Unlocked4StarSettings.Enabled ||
                  mysteryRaidsSettings.Unlocked5StarSettings.Enabled || mysteryRaidsSettings.Unlocked6StarSettings.Enabled))
            {
                Log("All Mystery Raids options are disabled. Mystery Raids will be turned off.");
                Settings.RaidSettings.MysteryRaids = false; // Disable Mystery Raids
                return; // Exit the method
            }

            // Create a list of enabled StoryProgressLevels
            var enabledLevels = new List<GameProgress>();
            if (mysteryRaidsSettings.Unlocked3StarSettings.Enabled) enabledLevels.Add(GameProgress.Unlocked3Stars);
            if (mysteryRaidsSettings.Unlocked4StarSettings.Enabled) enabledLevels.Add(GameProgress.Unlocked4Stars);
            if (mysteryRaidsSettings.Unlocked5StarSettings.Enabled) enabledLevels.Add(GameProgress.Unlocked5Stars);
            if (mysteryRaidsSettings.Unlocked6StarSettings.Enabled) enabledLevels.Add(GameProgress.Unlocked6Stars);

            // Randomly pick a StoryProgressLevel from the enabled levels
            GameProgress gameProgress = enabledLevels[random.Next(enabledLevels.Count)];

            // Initialize a list to store possible difficulties
            List<int> possibleDifficulties = new List<int>();

            // Determine possible difficulties based on the selected GameProgress
            switch (gameProgress)
            {
                case GameProgress.Unlocked3Stars:
                    if (mysteryRaidsSettings.Unlocked3StarSettings.Allow1StarRaids) possibleDifficulties.Add(1);
                    if (mysteryRaidsSettings.Unlocked3StarSettings.Allow2StarRaids) possibleDifficulties.Add(2);
                    if (mysteryRaidsSettings.Unlocked3StarSettings.Allow3StarRaids) possibleDifficulties.Add(3);
                    break;
                case GameProgress.Unlocked4Stars:
                    if (mysteryRaidsSettings.Unlocked4StarSettings.Allow1StarRaids) possibleDifficulties.Add(1);
                    if (mysteryRaidsSettings.Unlocked4StarSettings.Allow2StarRaids) possibleDifficulties.Add(2);
                    if (mysteryRaidsSettings.Unlocked4StarSettings.Allow3StarRaids) possibleDifficulties.Add(3);
                    if (mysteryRaidsSettings.Unlocked4StarSettings.Allow4StarRaids) possibleDifficulties.Add(4);
                    break;
                case GameProgress.Unlocked5Stars:
                    if (mysteryRaidsSettings.Unlocked5StarSettings.Allow3StarRaids) possibleDifficulties.Add(3);
                    if (mysteryRaidsSettings.Unlocked5StarSettings.Allow4StarRaids) possibleDifficulties.Add(4);
                    if (mysteryRaidsSettings.Unlocked5StarSettings.Allow5StarRaids) possibleDifficulties.Add(5);
                    break;
                case GameProgress.Unlocked6Stars:
                    if (mysteryRaidsSettings.Unlocked6StarSettings.Allow3StarRaids) possibleDifficulties.Add(3);
                    if (mysteryRaidsSettings.Unlocked6StarSettings.Allow4StarRaids) possibleDifficulties.Add(4);
                    if (mysteryRaidsSettings.Unlocked6StarSettings.Allow5StarRaids) possibleDifficulties.Add(5);
                    if (mysteryRaidsSettings.Unlocked6StarSettings.Allow6StarRaids) possibleDifficulties.Add(6);
                    break;
            }

            // Check if there are any enabled difficulty levels
            if (!possibleDifficulties.Any())
            {
                Log("No difficulty levels enabled for the selected Story Progress. Mystery Raids will be turned off.");
                Settings.RaidSettings.MysteryRaids = false; // Disable Mystery Raids
                return; // Exit the method
            }

            // Randomly pick a difficulty level from the possible difficulties
            int randomDifficultyLevel = possibleDifficulties[random.Next(possibleDifficulties.Count)];

            // Determine the crystal type based on difficulty level
            var crystalType = randomDifficultyLevel switch
            {
                >= 1 and <= 5 => TeraCrystalType.Base,
                6 => TeraCrystalType.Black,
                _ => throw new ArgumentException("Invalid difficulty level.")
            };

            // Create a new ActiveRaid entry for the random shiny raid
            RotatingRaidParameters newRandomShinyRaid = new RotatingRaidParameters
            {
                Seed = randomSeed.ToString("X8"),
                Species = Species.None,
                Title = "Mystery Shiny Raid",
                AddedByRACommand = true,
                DifficultyLevel = randomDifficultyLevel,
                StoryProgressLevel = (int)gameProgress,
                CrystalType = crystalType,
                IsShiny = true
            };

            // Find the last position of a raid added by the RA command
            int lastRaCommandRaidIndex = Settings.ActiveRaids.FindLastIndex(raid => raid.AddedByRACommand);
            int insertPosition = lastRaCommandRaidIndex != -1 ? lastRaCommandRaidIndex + 1 : RotationCount + 1;

            // Insert the new raid at the determined position
            Settings.ActiveRaids.Insert(insertPosition, newRandomShinyRaid);

            // Log the addition for debugging purposes
            Log($"Added Mystery Shiny Raid with seed: {randomSeed:X} at position {insertPosition}");
        }

        private static uint GenerateRandomShinySeed()
        {
            Random random = new Random();
            uint seed;

            do
            {
                // Generate a random uint
                byte[] buffer = new byte[4];
                random.NextBytes(buffer);
                seed = BitConverter.ToUInt32(buffer, 0);
            }
            while (Raidshiny(seed) == 0);

            return seed;
        }

        private static int Raidshiny(uint Seed)
        {
            Xoroshiro128Plus xoroshiro128Plus = new Xoroshiro128Plus(Seed);
            uint num = (uint)xoroshiro128Plus.NextInt(4294967295uL);
            uint num2 = (uint)xoroshiro128Plus.NextInt(4294967295uL);
            uint num3 = (uint)xoroshiro128Plus.NextInt(4294967295uL);
            return (((num3 >> 16) ^ (num3 & 0xFFFF)) >> 4 == ((num2 >> 16) ^ (num2 & 0xFFFF)) >> 4) ? 1 : 0;
        }

        private async Task SyncSeedToIndexZero(int index, string raidType, CancellationToken token)
        {
            if (index == -1)
                return;

            // The target index is always 0 as per the new requirement
            const int targetIndex = 0;

            // Determine pointers for the specified index and the target index
            List<long> ptrAtIndex = DeterminePointer(index); // Pointer for the current index
            List<long> ptrAtTarget = DeterminePointer(targetIndex); // Pointer for the target index (always 0)

            // Read the seed from the specified index
            var seedBytesAtIndex = await SwitchConnection.PointerPeek(4, ptrAtIndex, token).ConfigureAwait(false);
            uint seedAtIndex = BitConverter.ToUInt32(seedBytesAtIndex, 0);

            // Write the seed to the target index
            byte[] seedBytesToWrite = BitConverter.GetBytes(seedAtIndex);
            await SwitchConnection.PointerPoke(seedBytesToWrite, ptrAtTarget, token).ConfigureAwait(false);

            Log($"Synced seed from index {index} to index {targetIndex}");
        }

        private async Task SwapRaidLocationsAsync(int currentRaidIndex, string raidType, CancellationToken token)
        {
            // We always swap with index 0
            const int swapWithIndex = 0;

            // Get the pointers for the current raid index and index 0
            List<long> currentPointer = CalculateDirectPointer(currentRaidIndex);
            List<long> swapPointer = CalculateDirectPointer(swapWithIndex);

            int areaIdOffset = 20; // Corrected Area ID offset
            int denIdOffset = 25; // Corrected Den ID offset

            // Read and store area and den ID values for index 0
            if (!originalIdsSet)
            {
                areaIdIndex0 = await ReadValue("Area ID", 4, AdjustPointer(swapPointer, areaIdOffset), token);
                denIdIndex0 = await ReadValue("Den ID", 4, AdjustPointer(swapPointer, denIdOffset), token);
                originalIdsSet = true;
            }

            // Read values from current index
            uint currentAreaId = await ReadValue("Area ID", 4, AdjustPointer(currentPointer, areaIdOffset), token);
            uint currentDenId = await ReadValue("Den ID", 4, AdjustPointer(currentPointer, denIdOffset), token);

            if (!hasSwapped && (raidType == "Might" || raidType == "Distribution"))
            {
                // Perform swap
                await LogAndUpdateValue("Area ID", areaIdIndex0, 4, AdjustPointer(currentPointer, areaIdOffset), token);
                await LogAndUpdateValue("Den ID", denIdIndex0, 4, AdjustPointer(currentPointer, denIdOffset), token);
                await LogAndUpdateValue("Area ID", currentAreaId, 4, AdjustPointer(swapPointer, areaIdOffset), token);
                await LogAndUpdateValue("Den ID", currentDenId, 4, AdjustPointer(swapPointer, denIdOffset), token);

                originalAreaId = currentAreaId;
                originalDenId = currentDenId;
                originalIdsSet = true;
                hasSwapped = true;
            }
            else if (hasSwapped && (raidType != "Might" && raidType != "Distribution"))
            {
                // Reversing swap for other raid types
                Log("Reversing swap for non-Might/Distribution raid.");

                // Swap current index back with original IDs
                await LogAndUpdateValue("Area ID", originalAreaId, 4, AdjustPointer(currentPointer, areaIdOffset), token);
                await LogAndUpdateValue("Den ID", originalDenId, 4, AdjustPointer(currentPointer, denIdOffset), token);

                // Restore index 0 to its original state
                await LogAndUpdateValue("Area ID", areaIdIndex0, 4, AdjustPointer(swapPointer, areaIdOffset), token);
                await LogAndUpdateValue("Den ID", denIdIndex0, 4, AdjustPointer(swapPointer, denIdOffset), token);

                hasSwapped = false;
            }
        }

        private async Task<uint> ReadValue(string fieldName, int size, List<long> pointer, CancellationToken token)
        {
            byte[] valueBytes = await SwitchConnection.PointerPeek(size, pointer, token).ConfigureAwait(false);
            //  Log($"{fieldName} - Read Value: {BitConverter.ToString(valueBytes)}");

            // Determine the byte order based on the field name
            bool isBigEndian = fieldName.Equals("Den ID");

            if (isBigEndian)
            {
                // If the value is in big-endian format, reverse the byte array
                Array.Reverse(valueBytes);
            }

            // Convert the byte array to uint (now in little-endian format)
            return BitConverter.ToUInt32(valueBytes, 0);
        }

        private async Task LogAndUpdateValue(string fieldName, uint value, int size, List<long> pointer, CancellationToken token)
        {
            byte[] currentValue = await SwitchConnection.PointerPeek(size, pointer, token).ConfigureAwait(false);
            // Log($"{fieldName} - Current Value: {BitConverter.ToString(currentValue)}");

            // Determine the byte order based on the field name
            bool isBigEndian = fieldName.Equals("Den ID");

            // Create a new byte array for the new value
            byte[] newValue = new byte[4]; // Assuming uint is 4 bytes
            if (isBigEndian)
            {
                newValue[0] = (byte)(value >> 24); // Most significant byte
                newValue[1] = (byte)(value >> 16);
                newValue[2] = (byte)(value >> 8);
                newValue[3] = (byte)(value);       // Least significant byte
            }
            else
            {
                newValue[0] = (byte)(value);       // Least significant byte
                newValue[1] = (byte)(value >> 8);
                newValue[2] = (byte)(value >> 16);
                newValue[3] = (byte)(value >> 24); // Most significant byte
            }

            await SwitchConnection.PointerPoke(newValue, pointer, token).ConfigureAwait(false);

            byte[] updatedValue = await SwitchConnection.PointerPeek(size, pointer, token).ConfigureAwait(false);
            //  Log($"{fieldName} - Updated Value: {BitConverter.ToString(updatedValue)}");
        }

        private static List<long> AdjustPointer(List<long> basePointer, int offset)
        {
            var adjustedPointer = new List<long>(basePointer);
            adjustedPointer[3] += offset; // Adjusting the offset at the 4th index
            return adjustedPointer;
        }

        private List<long> CalculateDirectPointer(int index)
        {
            return new(Offsets.RaidBlockPointerP)
            {
                [3] = 0x40 + index * 0x20
            };
        }

        private List<long> DeterminePointer(int index)
        {
            if (index < 69)
            {
                return new List<long>(Offsets.RaidBlockPointerP)
                {
                    [3] = 0x40 + ((index + 1) * 0x20)
                };
            }
            else if (index < 94)
            {
                return new List<long>(Offsets.RaidBlockPointerK)
                {
                    [3] = 0xCE8 + ((index - 69) * 0x20)
                };
            }
            else
            {
                return new List<long>(Offsets.RaidBlockPointerB)
                {
                    [3] = 0x1968 + ((index - 93) * 0x20)
                };
            }
        }
        private async Task SanitizeRotationCount(CancellationToken token)
        {
            await Task.Delay(0_050, token).ConfigureAwait(false);

            if (Settings.ActiveRaids.Count == 0)
            {
                Log("ActiveRaids is empty. Exiting SanitizeRotationCount.");
                RotationCount = 0;
                return;
            }

            // Normalize RotationCount to be within the range of ActiveRaids
            RotationCount = (RotationCount >= Settings.ActiveRaids.Count) ? 0 : RotationCount;

            // Process RA command raids
            if (Settings.ActiveRaids[RotationCount].AddedByRACommand)
            {
                bool isMysteryRaid = Settings.ActiveRaids[RotationCount].Title.Contains("Mystery Shiny Raid");
                bool isUserRequestedRaid = !isMysteryRaid && Settings.ActiveRaids[RotationCount].Title.Contains("'s Requested Raid");

                if (isUserRequestedRaid || isMysteryRaid)
                {
                    Log($"Raid for {Settings.ActiveRaids[RotationCount].Species} was added via RA command and will be removed from the rotation list.");
                    Settings.ActiveRaids.RemoveAt(RotationCount);
                    RotationCount = (RotationCount >= Settings.ActiveRaids.Count) ? 0 : RotationCount;
                }
                else if (!firstRun)
                {
                    RotationCount = (RotationCount + 1) % Settings.ActiveRaids.Count;
                }
            }
            else if (!firstRun)
            {
                RotationCount = (RotationCount + 1) % Settings.ActiveRaids.Count;
            }

            if (firstRun)
            {
                RotationCount = 0;
                firstRun = false;
            }

            if (Settings.RaidSettings.RandomRotation)
            {
                ProcessRandomRotation();
                return;
            }

            // Find next priority raid
            RotationCount = FindNextPriorityRaidIndex(RotationCount, Settings.ActiveRaids);
            Log($"Next raid in the list: {Settings.ActiveRaids[RotationCount].Species}.");
        }

        private int FindNextPriorityRaidIndex(int currentRotationCount, List<RotatingRaidParameters> raids)
        {
            int count = raids.Count;

            // First, check for user-requested RA command raids that are not Mystery Shiny Raids
            for (int i = 0; i < count; i++)
            {
                int index = (currentRotationCount + i) % count;
                RotatingRaidParameters raid = raids[index];

                if (raid.AddedByRACommand && !raid.Title.Contains("Mystery Shiny Raid"))
                {
                    return index;
                }
            }
            // If no user-requested raids are found, check for Mystery Shiny Raids if enabled
            if (Settings.RaidSettings.MysteryRaids)
            {
                for (int i = 0; i < count; i++)
                {
                    int index = (currentRotationCount + i) % count;
                    RotatingRaidParameters raid = raids[index];

                    if (raid.Title.Contains("Mystery Shiny Raid"))
                    {
                        return index;
                    }
                }
            }

            // If no priority raids are found, return the current rotation count
            return currentRotationCount;
        }

        private void DisableMysteryRaidsIfEventActive()
        {
            if (Settings.EventSettings.EventActive)
            {
                bool settingsChanged = false;

                // Check and update settings for 3-star raids
                if (Settings.RaidSettings.MysteryRaidsSettings.Unlocked3StarSettings.Enabled)
                {
                    Settings.RaidSettings.MysteryRaidsSettings.Unlocked3StarSettings.Enabled = false;
                    settingsChanged = true;
                }

                // Check and update settings for 4-star raids
                if (Settings.RaidSettings.MysteryRaidsSettings.Unlocked4StarSettings.Enabled)
                {
                    Settings.RaidSettings.MysteryRaidsSettings.Unlocked4StarSettings.Enabled = false;
                    settingsChanged = true;
                }

                // Check and update settings for 5-star raids
                if (Settings.RaidSettings.MysteryRaidsSettings.Unlocked5StarSettings.Enabled)
                {
                    Settings.RaidSettings.MysteryRaidsSettings.Unlocked5StarSettings.Enabled = false;
                    settingsChanged = true;
                }

                if (settingsChanged)
                {
                    Log("Mystery Raids for 3, 4, and 5 stars have been disabled due to an active event.");
                }
            }
        }

        private void ProcessRandomRotation()
        {
            // Turn off RandomRotation if both RandomRotation and MysteryRaid are true
            if (Settings.RaidSettings.RandomRotation && Settings.RaidSettings.MysteryRaids)
            {
                Settings.RaidSettings.RandomRotation = false;
                Log("RandomRotation turned off due to MysteryRaids being active.");
                return;  // Exit the method as RandomRotation is now turned off
            }

            // Check the remaining raids for any added by the RA command
            for (var i = RotationCount; i < Settings.ActiveRaids.Count; i++)
            {
                if (Settings.ActiveRaids[i].AddedByRACommand)
                {
                    RotationCount = i;
                    Log($"Setting Rotation Count to {RotationCount}");
                    return;  // Exit method as a raid added by RA command was found
                }
            }

            // If no raid added by RA command was found, select a random raid
            var random = new Random();
            RotationCount = random.Next(Settings.ActiveRaids.Count);
            Log($"Setting Rotation Count to {RotationCount}");
        }

        private async Task InjectPartyPk(string battlepk, CancellationToken token)
        {
            var set = new ShowdownSet(battlepk);
            var template = AutoLegalityWrapper.GetTemplate(set);
            PK9 pk = (PK9)HostSAV.GetLegal(template, out _);
            pk.ResetPartyStats();
            var offset = await SwitchConnection.PointerAll(Offsets.BoxStartPokemonPointer, token).ConfigureAwait(false);
            await SwitchConnection.WriteBytesAbsoluteAsync(pk.EncryptedBoxData, offset, token).ConfigureAwait(false);
        }

        private async Task<bool> PrepareForRaid(CancellationToken token)
        {
            if (Settings.ActiveRaids[RotationCount].AddedByRACommand)
            {
                var user = Settings.ActiveRaids[RotationCount].User;

                // Determine if the raid is a "Free For All"
                bool isFreeForAll = !Settings.ActiveRaids[RotationCount].IsCoded || EmptyRaid >= Settings.LobbyOptions.EmptyRaidLimit;

                if (user != null && !isFreeForAll)
                {
                    try
                    {
                        // Only send the message if it's not a "Free For All"
                        await user.SendMessageAsync("Get Ready!  Your raid is about to start!").ConfigureAwait(false);
                    }
                    catch (Discord.Net.HttpException ex)
                    {
                        // Handle exception (e.g., log the error or send a message to a logging channel)
                        Log($"Failed to send DM to {user.Username}. They might have DMs turned off. Exception: {ex.Message}");
                    }
                }
            }

            Log("Preparing lobby...");
            LobbyFiltersCategory settings = new LobbyFiltersCategory();

            if (!await ConnectToOnline(Hub.Config, token))
            {
                return false;
            }

            await Task.Delay(0_500, token).ConfigureAwait(false);
            await Click(HOME, 0_500, token).ConfigureAwait(false);
            await Click(HOME, 0_500, token).ConfigureAwait(false);
            // Check if firstRun is false before injecting PartyPK
            if (!firstRun)
            {
                var len = string.Empty;
                foreach (var l in Settings.ActiveRaids[RotationCount].PartyPK)
                    len += l;
                if (len.Length > 1 && EmptyRaid == 0)
                {
                    Log("Preparing PartyPK. Sit tight.");
                    await Task.Delay(2_500 + settings.ExtraTimeLobbyDisband, token).ConfigureAwait(false);
                    await SetCurrentBox(0, token).ConfigureAwait(false);
                    var res = string.Join("\n", Settings.ActiveRaids[RotationCount].PartyPK);
                    if (res.Length > 4096)
                        res = res[..4096];
                    await InjectPartyPk(res, token).ConfigureAwait(false);

                    await Click(X, 2_000, token).ConfigureAwait(false);
                    await Click(DRIGHT, 0_500, token).ConfigureAwait(false);
                    await SetStick(SwitchStick.LEFT, 0, -32000, 1_000, token).ConfigureAwait(false);
                    await SetStick(SwitchStick.LEFT, 0, 0, 0, token).ConfigureAwait(false);
                    for (int i = 0; i < 2; i++)
                        await Click(DDOWN, 0_500, token).ConfigureAwait(false);
                    await Click(A, 3_500, token).ConfigureAwait(false);
                    await Click(Y, 0_500, token).ConfigureAwait(false);
                    await Click(DLEFT, 0_800, token).ConfigureAwait(false);
                    await Click(Y, 0_500, token).ConfigureAwait(false);
                    for (int i = 0; i < 2; i++)
                        await Click(B, 1_500, token).ConfigureAwait(false);
                    Log("PartyPK switch successful.");
                }
            }
            else
            {
                Log("First run detected, skipping PartyPK injection.");
            }

            for (int i = 0; i < 4; i++)
                await Click(B, 1_000, token).ConfigureAwait(false);

            await Task.Delay(1_500, token).ConfigureAwait(false);

            if (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                return false;

            await Click(A, 3_000, token).ConfigureAwait(false);
            await Click(A, 3_000, token).ConfigureAwait(false);

            if (firstRun)
            {
                Log("First Run detected. Opening Lobby up to all to start raid rotation.");
                await Click(DDOWN, 1_000, token).ConfigureAwait(false);
            }
            else if (!Settings.ActiveRaids[RotationCount].IsCoded || (Settings.ActiveRaids[RotationCount].IsCoded && EmptyRaid == Settings.LobbyOptions.EmptyRaidLimit && Settings.LobbyOptions.LobbyMethod == LobbyMethodOptions.OpenLobby))
            {
                if (Settings.ActiveRaids[RotationCount].IsCoded && EmptyRaid == Settings.LobbyOptions.EmptyRaidLimit && Settings.LobbyOptions.LobbyMethod == LobbyMethodOptions.OpenLobby)
                    Log($"We had {Settings.LobbyOptions.EmptyRaidLimit} empty raids.. Opening this raid to all!");
                await Click(DDOWN, 1_000, token).ConfigureAwait(false);
            }
            else
            {
                await Click(A, 3_000, token).ConfigureAwait(false);
            }

            await Click(A, 8_000, token).ConfigureAwait(false);
            return true;
        }

        private async Task RollBackTime(CancellationToken token)
        {
            for (int i = 0; i < 2; i++)
                await Click(B, 0_150, token).ConfigureAwait(false);

            for (int i = 0; i < 2; i++)
                await Click(DRIGHT, 0_150, token).ConfigureAwait(false);
            await Click(DDOWN, 0_150, token).ConfigureAwait(false);
            await Click(DRIGHT, 0_150, token).ConfigureAwait(false);
            await Click(A, 1_250, token).ConfigureAwait(false); // Enter settings

            await PressAndHold(DDOWN, 2_000, 0_250, token).ConfigureAwait(false); // Scroll to system settings
            await Click(A, 1_250, token).ConfigureAwait(false);

            if (Settings.MiscSettings.UseOvershoot)
            {
                await PressAndHold(DDOWN, Settings.MiscSettings.HoldTimeForRollover, 1_000, token).ConfigureAwait(false);
                await Click(DUP, 0_500, token).ConfigureAwait(false);
            }
            else if (!Settings.MiscSettings.UseOvershoot)
            {
                for (int i = 0; i < 39; i++)
                    await Click(DDOWN, 0_100, token).ConfigureAwait(false);
            }

            await Click(A, 1_250, token).ConfigureAwait(false);
            for (int i = 0; i < 2; i++)
                await Click(DDOWN, 0_150, token).ConfigureAwait(false);
            await Click(A, 0_500, token).ConfigureAwait(false);

            for (int i = 0; i < 3; i++) // Navigate to the hour setting
                await Click(DRIGHT, 0_200, token).ConfigureAwait(false);

            for (int i = 0; i < 5; i++) // Roll back the hour by 5
                await Click(DDOWN, 0_200, token).ConfigureAwait(false);

            for (int i = 0; i < 8; i++) // Mash DRIGHT to confirm
                await Click(DRIGHT, 0_200, token).ConfigureAwait(false);

            await Click(A, 0_200, token).ConfigureAwait(false); // Confirm date/time change
            await Click(HOME, 1_000, token).ConfigureAwait(false); // Back to title screen
        }

        private async Task<bool> GetLobbyReady(bool recovery, CancellationToken token)
        {
            var x = 0;
            Log("Connecting to lobby...");
            while (!await IsConnectedToLobby(token).ConfigureAwait(false))
            {
                await Click(A, 1_000, token).ConfigureAwait(false);
                x++;
                if (x == 15 && recovery)
                {
                    Log("No den here! Rolling again.");
                    return false;
                }
                if (x == 45)
                {
                    Log("Failed to connect to lobby, restarting game incase we were in battle/bad connection.");
                    LobbyError++;
                    await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
                    Log("Attempting to restart routine!");
                    return false;
                }
            }
            return true;
        }

        private async Task<string> GetRaidCode(CancellationToken token)
        {
            var data = await SwitchConnection.PointerPeek(6, Offsets.TeraRaidCodePointer, token).ConfigureAwait(false);
            TeraRaidCode = Encoding.ASCII.GetString(data).ToLower(); // Convert to lowercase for easier reading
            Log($"Raid Code: {TeraRaidCode}");
            return $"\n{TeraRaidCode}\n";
        }

        private async Task<bool> CheckIfTrainerBanned(RaidMyStatus trainer, ulong nid, int player, CancellationToken token)
        {
            if (!RaidTracker.ContainsKey(nid))
                RaidTracker.Add(nid, 0);

            var msg = string.Empty;
            var banResultCFW = RaiderBanList.List.FirstOrDefault(x => x.ID == nid);
            bool isBanned = banResultCFW != default;

            if (isBanned)
            {
                msg = $"{banResultCFW!.Name} was found in the host's ban list.\n{banResultCFW.Comment}";
                Log(msg);
                await EnqueueEmbed(null, msg, false, true, false, false, token).ConfigureAwait(false);
                return true;
            }
            return false;
        }

        private async Task<(bool, List<(ulong, RaidMyStatus)>)> ReadTrainers(CancellationToken token)
        {
            await EnqueueEmbed(null, "", false, false, false, false, token).ConfigureAwait(false);

            List<(ulong, RaidMyStatus)> lobbyTrainers = new();
            var wait = TimeSpan.FromSeconds(Settings.RaidSettings.TimeToWait);
            var endTime = DateTime.Now + wait;
            bool full = false;

            while (!full && DateTime.Now < endTime)
            {
                for (int i = 0; i < 3; i++)
                {
                    var player = i + 2;
                    Log($"Waiting for Player {player} to load...");

                    var nidOfs = TeraNIDOffsets[i];
                    var data = await SwitchConnection.ReadBytesAbsoluteAsync(nidOfs, 8, token).ConfigureAwait(false);
                    var nid = BitConverter.ToUInt64(data, 0);
                    while (nid == 0 && DateTime.Now < endTime)
                    {
                        await Task.Delay(0_500, token).ConfigureAwait(false);
                        data = await SwitchConnection.ReadBytesAbsoluteAsync(nidOfs, 8, token).ConfigureAwait(false);
                        nid = BitConverter.ToUInt64(data, 0);
                    }

                    List<long> ptr = new(Offsets.Trader2MyStatusPointer);
                    ptr[2] += i * 0x30;
                    var trainer = await GetTradePartnerMyStatus(ptr, token).ConfigureAwait(false);

                    while (trainer.OT.Length == 0 && DateTime.Now < endTime)
                    {
                        await Task.Delay(0_500, token).ConfigureAwait(false);
                        trainer = await GetTradePartnerMyStatus(ptr, token).ConfigureAwait(false);
                    }

                    if (nid != 0 && !string.IsNullOrWhiteSpace(trainer.OT))
                    {
                        if (await CheckIfTrainerBanned(trainer, nid, player, token).ConfigureAwait(false))
                            return (false, lobbyTrainers);
                    }

                    // Check if the NID is already in the list to prevent duplicates
                    if (lobbyTrainers.Any(x => x.Item1 == nid))
                    {
                        Log($"Duplicate NID detected: {nid}. Skipping...");
                        continue; // Skip adding this NID if it's a duplicate
                    }

                    // If NID is not a duplicate and has a valid trainer OT, add to the list
                    if (nid > 0 && trainer.OT.Length > 0)
                        lobbyTrainers.Add((nid, trainer));

                    full = lobbyTrainers.Count == 3;
                    if (full || DateTime.Now >= endTime)
                        break;
                }
            }

            await Task.Delay(5_000, token).ConfigureAwait(false);

            if (lobbyTrainers.Count == 0)
            {
                EmptyRaid++;
                LostRaid++;
                Log($"Nobody joined the raid, recovering...");
                if (Settings.LobbyOptions.LobbyMethod == LobbyMethodOptions.OpenLobby)
                    Log($"Empty Raid Count #{EmptyRaid}");
                if (Settings.LobbyOptions.LobbyMethod == LobbyMethodOptions.SkipRaid)
                    Log($"Lost/Empty Lobbies: {LostRaid}/{Settings.LobbyOptions.SkipRaidLimit}");

                return (false, lobbyTrainers);
            }

            RaidCount++; // Increment RaidCount only when a raid is actually starting.
            Log($"Raid #{RaidCount} is starting!");
            if (EmptyRaid != 0)
                EmptyRaid = 0;
            return (true, lobbyTrainers);
        }

        private async Task<bool> IsConnectedToLobby(CancellationToken token)
        {
            var data = await SwitchConnection.ReadBytesMainAsync(Offsets.TeraLobbyIsConnected, 1, token).ConfigureAwait(false);
            return data[0] != 0x00; // 0 when in lobby but not connected
        }

        private async Task<bool> IsInRaid(CancellationToken token)
        {
            var data = await SwitchConnection.ReadBytesMainAsync(Offsets.LoadedIntoDesiredState, 1, token).ConfigureAwait(false);
            return data[0] == 0x02; // 2 when in raid, 1 when not
        }

        private async Task AdvanceDaySV(CancellationToken token)
        {
            var scrollroll = Settings.MiscSettings.DateTimeFormat switch
            {
                DTFormat.DDMMYY => 0,
                DTFormat.YYMMDD => 2,
                _ => 1,
            };

            for (int i = 0; i < 2; i++)
                await Click(B, 0_150, token).ConfigureAwait(false);

            for (int i = 0; i < 2; i++)
                await Click(DRIGHT, 0_150, token).ConfigureAwait(false);
            await Click(DDOWN, 0_150, token).ConfigureAwait(false);
            await Click(DRIGHT, 0_150, token).ConfigureAwait(false);
            await Click(A, 1_250, token).ConfigureAwait(false); // Enter settings

            await PressAndHold(DDOWN, 2_000, 0_250, token).ConfigureAwait(false); // Scroll to system settings
            await Click(A, 1_250, token).ConfigureAwait(false);

            if (Settings.MiscSettings.UseOvershoot)
            {
                await PressAndHold(DDOWN, Settings.MiscSettings.HoldTimeForRollover, 1_000, token).ConfigureAwait(false);
                await Click(DUP, 0_500, token).ConfigureAwait(false);
            }
            else if (!Settings.MiscSettings.UseOvershoot)
            {
                for (int i = 0; i < 39; i++)
                    await Click(DDOWN, 0_100, token).ConfigureAwait(false);
            }

            await Click(A, 1_250, token).ConfigureAwait(false);
            for (int i = 0; i < 2; i++)
                await Click(DDOWN, 0_150, token).ConfigureAwait(false);
            await Click(A, 0_500, token).ConfigureAwait(false);
            for (int i = 0; i < scrollroll; i++) // 0 to roll day for DDMMYY, 1 to roll day for MMDDYY, 3 to roll hour
                await Click(DRIGHT, 0_200, token).ConfigureAwait(false);

            await Click(DUP, 0_200, token).ConfigureAwait(false); // Advance a day

            for (int i = 0; i < 8; i++) // Mash DRIGHT to confirm
                await Click(DRIGHT, 0_200, token).ConfigureAwait(false);

            await Click(A, 0_200, token).ConfigureAwait(false); // Confirm date/time change
            await Click(HOME, 1_000, token).ConfigureAwait(false); // Back to title screen

            await Click(A, 0_200, token).ConfigureAwait(false); // Back in Game
        }

        private async Task RolloverCorrectionSV(CancellationToken token)
        {
            var scrollroll = Settings.MiscSettings.DateTimeFormat switch
            {
                DTFormat.DDMMYY => 0,
                DTFormat.YYMMDD => 2,
                _ => 1,
            };

            for (int i = 0; i < 2; i++)
                await Click(B, 0_150, token).ConfigureAwait(false);

            for (int i = 0; i < 2; i++)
                await Click(DRIGHT, 0_150, token).ConfigureAwait(false);
            await Click(DDOWN, 0_150, token).ConfigureAwait(false);
            await Click(DRIGHT, 0_150, token).ConfigureAwait(false);
            await Click(A, 1_250, token).ConfigureAwait(false); // Enter settings

            await PressAndHold(DDOWN, 2_000, 0_250, token).ConfigureAwait(false); // Scroll to system settings
            await Click(A, 1_250, token).ConfigureAwait(false);

            if (Settings.MiscSettings.UseOvershoot)
            {
                await PressAndHold(DDOWN, Settings.MiscSettings.HoldTimeForRollover, 1_000, token).ConfigureAwait(false);
                await Click(DUP, 0_500, token).ConfigureAwait(false);
            }
            else if (!Settings.MiscSettings.UseOvershoot)
            {
                for (int i = 0; i < 39; i++)
                    await Click(DDOWN, 0_100, token).ConfigureAwait(false);
            }

            await Click(A, 1_250, token).ConfigureAwait(false);
            for (int i = 0; i < 2; i++)
                await Click(DDOWN, 0_150, token).ConfigureAwait(false);
            await Click(A, 0_500, token).ConfigureAwait(false);
            for (int i = 0; i < scrollroll; i++) // 0 to roll day for DDMMYY, 1 to roll day for MMDDYY, 3 to roll hour
                await Click(DRIGHT, 0_200, token).ConfigureAwait(false);

            await Click(DDOWN, 0_200, token).ConfigureAwait(false);

            for (int i = 0; i < 8; i++) // Mash DRIGHT to confirm
                await Click(DRIGHT, 0_200, token).ConfigureAwait(false);

            await Click(A, 0_200, token).ConfigureAwait(false); // Confirm date/time change
            await Click(HOME, 1_000, token).ConfigureAwait(false); // Back to title screen
        }

        private async Task RegroupFromBannedUser(CancellationToken token)
        {
            Log("Attempting to remake lobby..");
            await Click(B, 2_000, token).ConfigureAwait(false);
            await Click(A, 3_000, token).ConfigureAwait(false);
            await Click(A, 3_000, token).ConfigureAwait(false);
            await Click(B, 1_000, token).ConfigureAwait(false);
        }

        private async Task InitializeSessionOffsets(CancellationToken token)
        {
            Log("Caching session offsets...");
            OverworldOffset = await SwitchConnection.PointerAll(Offsets.OverworldPointer, token).ConfigureAwait(false);
            ConnectedOffset = await SwitchConnection.PointerAll(Offsets.IsConnectedPointer, token).ConfigureAwait(false);
            RaidBlockPointerP = await SwitchConnection.PointerAll(Offsets.RaidBlockPointerP, token).ConfigureAwait(false);
            RaidBlockPointerK = await SwitchConnection.PointerAll(Offsets.RaidBlockPointerK, token).ConfigureAwait(false);
            if (firstRun)
            {
                GameProgress = await ReadGameProgress(token).ConfigureAwait(false);
                Log($"Current Game Progress identified as {GameProgress}.");
                currentSpawnsEnabled = (bool?)await ReadBlock(RaidDataBlocks.KWildSpawnsEnabled, CancellationToken.None);
                Log($"Current Overworld Spawn State {currentSpawnsEnabled}.");
            }

            var nidPointer = new long[] { Offsets.LinkTradePartnerNIDPointer[0], Offsets.LinkTradePartnerNIDPointer[1], Offsets.LinkTradePartnerNIDPointer[2] };
            for (int p = 0; p < TeraNIDOffsets.Length; p++)
            {
                nidPointer[2] = Offsets.LinkTradePartnerNIDPointer[2] + p * 0x8;
                TeraNIDOffsets[p] = await SwitchConnection.PointerAll(nidPointer, token).ConfigureAwait(false);
            }
            Log("Caching offsets complete!");
        }

        private static async Task<bool> IsValidImageUrlAsync(string url)
        {
            using (var httpClient = new HttpClient())
            {
                try
                {
                    var response = await httpClient.GetAsync(url);
                    return response.IsSuccessStatusCode;
                }
                catch (HttpRequestException ex) when (ex.InnerException is WebException webEx && webEx.Status == WebExceptionStatus.TrustFailure)
                {

                }
                catch (Exception ex)
                {

                }
                return false;
            }
        }

        readonly Dictionary<string, string> TypeAdvantages = new()
        {
            { "normal", "Fighting" },
            { "fire", "Water, Ground, Rock" },
            { "water", "Electric, Grass" },
            { "grass", "Flying, Poison, Bug, Fire, Ice" },
            { "electric", "Ground" },
            { "ice", "Fighting, Rock, Steel, Fire" },
            { "fighting", "Flying, Psychic, Fairy" },
            { "poison", "Ground, Psychic" },
            { "ground", "Water, Ice, Grass" },
            { "flying", "Rock, Electric, Ice" },
            { "psychic", "Bug, Ghost, Dark" },
            { "bug", "Flying, Rock, Fire" },
            { "rock", "Fighting, Ground, Steel, Water, Grass" },
            { "ghost", "Ghost, Dark" },
            { "dragon", "Ice, Dragon, Fairy" },
            { "dark", "Fighting, Bug, Fairy" },
            { "steel", "Fighting, Ground, Fire" },
            { "fairy", "Poison, Steel" }
        };

        private string GetTypeAdvantage(string teraType)
        {
            // Check if the type exists in the dictionary and return the corresponding advantage
            if (TypeAdvantages.TryGetValue(teraType.ToLower(), out string advantage))
            {
                return advantage;
            }
            return "Unknown Type";  // Return "Unknown Type" if the type doesn't exist in our dictionary
        }

        private async Task EnqueueEmbed(List<string>? names, string message, bool hatTrick, bool disband, bool upnext, bool raidstart, CancellationToken token)
        {
            if (firstRun)
            {
                // First Run detected. Not sending the embed to start raid rotation.
                return;
            }

            string code = string.Empty;

            // Determine if the raid is a "Free For All" based on the settings and conditions
            if (Settings.ActiveRaids[RotationCount].IsCoded && EmptyRaid < Settings.LobbyOptions.EmptyRaidLimit)
            {
                // If it's not a "Free For All", retrieve the raid code
                code = await GetRaidCode(token).ConfigureAwait(false);
            }
            else
            {
                // If it's a "Free For All", set the code as such
                code = "Free For All";
            }

            // Apply delay only if the raid was added by RA command, not a Mystery Shiny Raid, and has a code
            if (Settings.ActiveRaids[RotationCount].AddedByRACommand &&
                Settings.ActiveRaids[RotationCount].Title != "Mystery Shiny Raid" &&
                code != "Free For All")
            {
                await Task.Delay(Settings.EmbedToggles.RequestEmbedTime * 1000).ConfigureAwait(false);
            }

            // Description can only be up to 4096 characters.
            //var description = Settings.ActiveRaids[RotationCount].Description.Length > 0 ? string.Join("\n", Settings.ActiveRaids[RotationCount].Description) : "";
            var description = Settings.EmbedToggles.RaidEmbedDescription.Length > 0 ? string.Join("\n", Settings.EmbedToggles.RaidEmbedDescription) : "";
            if (description.Length > 4096) description = description[..4096];

            if (EmptyRaid == Settings.LobbyOptions.EmptyRaidLimit && Settings.LobbyOptions.LobbyMethod == LobbyMethodOptions.OpenLobby)
                EmptyRaid = 0;

            if (disband) // Wait for trainer to load before disband
                await Task.Delay(5_000, token).ConfigureAwait(false);

            byte[]? bytes = Array.Empty<byte>();
            if (Settings.EmbedToggles.TakeScreenshot && !upnext)
                try
                {
                    bytes = await SwitchConnection.PixelPeek(token).ConfigureAwait(false) ?? Array.Empty<byte>();
                }
                catch (Exception ex)
                {
                    Log($"Error while fetching pixels: {ex.Message}");
                }

            string disclaimer = Settings.ActiveRaids.Count > 1
                                ? $"NRB {NotRaidBot.Version} - notpaldea.net"
                                : "";

            var turl = string.Empty;
            var form = string.Empty;

            Log($"Rotation Count: {RotationCount} | Species is {Settings.ActiveRaids[RotationCount].Species}");
            PK9 pk = new()
            {
                Species = (ushort)Settings.ActiveRaids[RotationCount].Species,
                Form = (byte)Settings.ActiveRaids[RotationCount].SpeciesForm
            };
            if (pk.Form != 0)
                form = $"-{pk.Form}";
            if (Settings.ActiveRaids[RotationCount].IsShiny == true)
                pk.SetIsShiny(true);
            else
                pk.SetIsShiny(false);

            if (Settings.ActiveRaids[RotationCount].SpriteAlternateArt && Settings.ActiveRaids[RotationCount].IsShiny)
            {
                var altUrl = AltPokeImg(pk);

                try
                {
                    // Check if AltPokeImg URL is valid
                    if (await IsValidImageUrlAsync(altUrl))
                    {
                        turl = altUrl;
                    }
                    else
                    {
                        Settings.ActiveRaids[RotationCount].SpriteAlternateArt = false;  // Set SpriteAlternateArt to false if no img found
                        turl = RaidExtensions<PK9>.PokeImg(pk, false, false);
                        Log($"AltPokeImg URL was not valid. Setting SpriteAlternateArt to false.");
                    }
                }
                catch (Exception ex)
                {
                    // Log the exception and use the default sprite
                    Log($"Error while validating alternate image URL: {ex.Message}");
                    Settings.ActiveRaids[RotationCount].SpriteAlternateArt = false;  // Set SpriteAlternateArt to false due to error
                    turl = RaidExtensions<PK9>.PokeImg(pk, false, false);
                }
            }
            else
            {
                turl = RaidExtensions<PK9>.PokeImg(pk, false, false);
            }

            if (Settings.ActiveRaids[RotationCount].Species is 0)
                turl = "https://genpkm.com/images/combat.png";

            // Fetch the dominant color from the image only AFTER turl is assigned
            (int R, int G, int B) dominantColor = RaidExtensions<PK9>.GetDominantColor(turl);

            // Use the dominant color, unless it's a disband or hatTrick situation
            var embedColor = disband ? Color.Red : hatTrick ? Color.Purple : new Color(dominantColor.R, dominantColor.G, dominantColor.B);

            TimeSpan duration = new TimeSpan(0, 2, 31);

            // Calculate the future time by adding the duration to the current time
            DateTimeOffset futureTime = DateTimeOffset.Now.Add(duration);

            // Convert the future time to Unix timestamp
            long futureUnixTime = futureTime.ToUnixTimeSeconds();

            // Create the future time message using Discord's timestamp formatting
            string futureTimeMessage = $"**Raid Posting: <t:{futureUnixTime}:R>**";

            // Initialize the EmbedBuilder object
            var embed = new EmbedBuilder()
            {
                Title = disband ? $"**Raid canceled: [{TeraRaidCode}]**" : upnext && Settings.RaidSettings.TotalRaidsToHost != 0 ? $"Raid Ended - Preparing Next Raid!" : upnext && Settings.RaidSettings.TotalRaidsToHost == 0 ? $"Raid Ended - Preparing Next Raid!" : "",
                Color = embedColor,
                Description = disband ? message : upnext ? Settings.RaidSettings.TotalRaidsToHost == 0 ? $"# {Settings.ActiveRaids[RotationCount].Title}\n\n{futureTimeMessage}" : $"# {Settings.ActiveRaids[RotationCount].Title}\n\n{futureTimeMessage}" : raidstart ? "" : description,
                ImageUrl = bytes.Length > 0 ? "attachment://zap.jpg" : default,
            };

            // Only include footer if not posting 'upnext' embed with the 'Preparing Raid' title
            if (!(upnext && Settings.RaidSettings.TotalRaidsToHost == 0))
            {
                string programIconUrl = $"https://genpkm.com/images/icon4.png";
                int raidsInRotationCount = Hub.Config.RotatingRaidSV.ActiveRaids.Count(r => !r.AddedByRACommand);
                // Calculate uptime
                TimeSpan uptime = DateTime.Now - StartTime;

                // Check for singular or plural days/hours
                string dayLabel = uptime.Days == 1 ? "day" : "days";
                string hourLabel = uptime.Hours == 1 ? "hour" : "hours";
                string minuteLabel = uptime.Minutes == 1 ? "minute" : "minutes";

                // Format the uptime string, omitting the part if the value is 0
                string uptimeFormatted = "";
                if (uptime.Days > 0)
                {
                    uptimeFormatted += $"{uptime.Days} {dayLabel} ";
                }
                if (uptime.Hours > 0 || uptime.Days > 0) // Show hours if there are any hours, or if there are days even if hours are 0
                {
                    uptimeFormatted += $"{uptime.Hours} {hourLabel} ";
                }
                if (uptime.Minutes > 0 || uptime.Hours > 0 || uptime.Days > 0) // Show minutes if there are any minutes, or if there are hours/days even if minutes are 0
                {
                    uptimeFormatted += $"{uptime.Minutes} {minuteLabel}";
                }

                // Trim any excess whitespace from the string
                uptimeFormatted = uptimeFormatted.Trim();
                embed.WithFooter(new EmbedFooterBuilder()
                {
                    Text = $"Completed Raids: {RaidCount} (W: {WinCount} | L: {LossCount})\nActiveRaids: {raidsInRotationCount} | Uptime: {uptimeFormatted}\n" + disclaimer,
                    IconUrl = programIconUrl
                });
            }

            // Prepare the tera icon URL
            string teraType = RaidEmbedInfo.RaidSpeciesTeraType.ToLower();
            string folderName = Settings.EmbedToggles.SelectedTeraIconType == TeraIconType.Icon1 ? "icon1" : "icon2"; // Add more conditions for more icon types
            string teraIconUrl = $"https://genpkm.com/images/teraicons/{folderName}/{teraType}.png";

            // Only include author (header) if not posting 'upnext' embed with the 'Preparing Raid' title
            if (!(upnext && Settings.RaidSettings.TotalRaidsToHost == 0))
            {
                // Set the author (header) of the embed with the tera icon
                embed.WithAuthor(new EmbedAuthorBuilder()
                {
                    Name = RaidEmbedInfo.RaidEmbedTitle,
                    IconUrl = teraIconUrl
                });
            }
            if (!disband && !upnext && !raidstart)
            {
                StringBuilder statsField = new StringBuilder();
                statsField.AppendLine($"**Gender**: {RaidEmbedInfo.RaidSpeciesGender}");
                statsField.AppendLine($"**Nature**: {RaidEmbedInfo.RaidSpeciesNature}");
                statsField.AppendLine($"**Ability**: {RaidEmbedInfo.RaidSpeciesAbility}");
                statsField.AppendLine($"**IVs**: {RaidEmbedInfo.RaidSpeciesIVs}");
                statsField.AppendLine($"**Scale**: {RaidEmbedInfo.ScaleText}({RaidEmbedInfo.ScaleNumber})");

                if (Settings.EmbedToggles.IncludeSeed)
                {
                    statsField.AppendLine($"**Seed**: `{Settings.ActiveRaids[RotationCount].Seed}`");
                }

                embed.AddField("**__Stats__**", statsField.ToString(), true);
                embed.AddField("\u200b", "\u200b", true);
            }

            if (!disband && !upnext && !raidstart && Settings.EmbedToggles.IncludeMoves)
            {
                embed.AddField("**__Moves__**", string.IsNullOrEmpty($"{RaidEmbedInfo.ExtraMoves}") ? string.IsNullOrEmpty($"{RaidEmbedInfo.Moves}") ? "No Moves To Display" : $"{RaidEmbedInfo.Moves}" : $"{RaidEmbedInfo.Moves}\n**Extra Moves:**\n{RaidEmbedInfo.ExtraMoves}", true);
                RaidEmbedInfo.ExtraMoves = string.Empty;
            }

            if (!disband && !upnext && !raidstart && !Settings.EmbedToggles.IncludeMoves)
            {
                embed.AddField(" **__Special Rewards__**", string.IsNullOrEmpty($"{RaidEmbedInfo.SpecialRewards}") ? "No Rewards To Display" : $"{RaidEmbedInfo.SpecialRewards}", true);
                RaidEmbedInfo.SpecialRewards = string.Empty;
            }
            // Fetch the type advantage using the static RaidSpeciesTeraType from RaidEmbedInfo
            string typeAdvantage = GetTypeAdvantage(RaidEmbedInfo.RaidSpeciesTeraType);

            // Only include the Type Advantage if not posting 'upnext' embed with the 'Preparing Raid' title and if the raid isn't starting or disbanding
            if (!disband && !upnext && !raidstart && Settings.EmbedToggles.IncludeTypeAdvantage)
            {
                embed.AddField(" **__Type Advantage__**", typeAdvantage, true);
                embed.AddField("\u200b", "\u200b", true);
            }

            if (!disband && !upnext && !raidstart && Settings.EmbedToggles.IncludeMoves)
            {
                embed.AddField(" **__Special Rewards__**", string.IsNullOrEmpty($"{RaidEmbedInfo.SpecialRewards}") ? "No Rewards To Display" : $"{RaidEmbedInfo.SpecialRewards}", true);
                RaidEmbedInfo.SpecialRewards = string.Empty;
            }
            if (!disband && names is null && !upnext)
            {
                embed.AddField(Settings.EmbedToggles.IncludeCountdown ? $"**__Raid Starting__**:\n**<t:{DateTimeOffset.Now.ToUnixTimeSeconds() + Settings.RaidSettings.TimeToWait}:R>**" : $"**Waiting in lobby!**", $"Raid Code: **{code}**", true);
            }
            if (!disband && names is not null && !upnext)
            {
                var players = string.Empty;
                if (names.Count == 0)
                    players = "Our party dipped on us :/";
                else
                {
                    int i = 2;
                    names.ForEach(x =>
                    {
                        players += $"Player {i} - **{x}**\n";
                        i++;
                    });
                }

                embed.AddField($"**Raid #{RaidCount} is starting!**", players);
            }
            var fileName = $"raidecho{RotationCount}.jpg";
            embed.ThumbnailUrl = turl;
            embed.WithImageUrl($"attachment://{fileName}");
            EchoUtil.RaidEmbed(bytes, fileName, embed);
        }

        private async Task<bool> ConnectToOnline(PokeRaidHubConfig config, CancellationToken token)
        {
            int attemptCount = 0;
            const int maxAttempt = 5;
            const int waitTime = 10; // time in minutes to wait after max attempts

            while (true) // Loop until a successful connection is made or the task is canceled
            {
                try
                {
                    if (await IsConnectedOnline(ConnectedOffset, token).ConfigureAwait(false))
                    {
                        Log("Connection established successfully.");
                        break; // Exit the loop if connected successfully
                    }

                    if (attemptCount >= maxAttempt)
                    {
                        Log($"Failed to connect after {maxAttempt} attempts. Assuming a softban. Initiating wait for {waitTime} minutes before retrying.");
                        // Log details about sending an embed message
                        Log("Sending an embed message to notify about technical difficulties.");
                        EmbedBuilder embed = new EmbedBuilder
                        {
                            Title = "Experiencing Technical Difficulties",
                            Description = "The bot is experiencing issues connecting online. Please stand by as we try to resolve the issue.",
                            Color = Color.Red,
                            ThumbnailUrl = "https://genpkm.com/images/x.png"
                        };
                        EchoUtil.RaidEmbed(null, "", embed);
                        // Waiting process
                        await Click(B, 0_500, token).ConfigureAwait(false);
                        await Click(B, 0_500, token).ConfigureAwait(false);
                        Log($"Waiting for {waitTime} minutes before attempting to reconnect.");
                        await Task.Delay(TimeSpan.FromMinutes(waitTime), token).ConfigureAwait(false);
                        Log("Attempting to reopen the game.");
                        await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
                        attemptCount = 0; // Reset attempt count
                    }

                    attemptCount++;
                    Log($"Attempt {attemptCount} of {maxAttempt}: Trying to connect online...");

                    // Connection attempt logic
                    await Click(X, 3_000, token).ConfigureAwait(false);
                    await Click(L, 5_000 + config.Timings.ExtraTimeConnectOnline, token).ConfigureAwait(false);

                    // Wait a bit before rechecking the connection status
                    await Task.Delay(5000, token).ConfigureAwait(false); // Wait 5 seconds before rechecking

                    if (attemptCount < maxAttempt)
                    {
                        Log("Rechecking the online connection status...");
                        // Wait and recheck logic
                        await Click(B, 0_500, token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Log($"Exception occurred during connection attempt: {ex.Message}");
                    // Handle exceptions, like connectivity issues here

                    if (attemptCount >= maxAttempt)
                    {
                        Log($"Failed to connect after {maxAttempt} attempts due to exception. Waiting for {waitTime} minutes before retrying.");
                        await Task.Delay(TimeSpan.FromMinutes(waitTime), token).ConfigureAwait(false);
                        Log("Attempting to reopen the game.");
                        await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
                        attemptCount = 0;
                    }
                }
            }

            // Final steps after connection is established
            await Task.Delay(3_000 + config.Timings.ExtraTimeConnectOnline, token).ConfigureAwait(false);
            await Click(B, 0_500, token).ConfigureAwait(false);
            await Click(B, 0_500, token).ConfigureAwait(false);
            await Click(B, 0_500, token).ConfigureAwait(false);
            await Click(B, 0_500, token).ConfigureAwait(false);
            await Task.Delay(3_000, token).ConfigureAwait(false);

            return true;
        }

        private async Task<bool> RecoverToOverworld(CancellationToken token)
        {
            if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                return true;

            Log("Attempting to recover to overworld.");
            var attempts = 0;
            while (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
            {
                attempts++;
                if (attempts >= 30)
                    break;

                await Click(B, 1_300, token).ConfigureAwait(false);
                if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                    break;

                await Click(B, 2_000, token).ConfigureAwait(false);
                if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                    break;

                await Click(A, 1_300, token).ConfigureAwait(false);
                if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                    break;
            }

            // We didn't make it for some reason.
            if (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
            {
                Log("Failed to recover to overworld, rebooting the game.");
                await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
            }
            await Task.Delay(1_000, token).ConfigureAwait(false);
            return true;
        }

        public async Task StartGameRaid(PokeRaidHubConfig config, CancellationToken token)
        {
            // First, check if the time rollback feature is enabled
            if (Settings.RaidSettings.EnableTimeRollBack && DateTime.Now - TimeForRollBackCheck >= TimeSpan.FromHours(5))
            {
                Log("Rolling Time back 5 hours.");
                // Call the RollBackTime function
                await RollBackTime(token).ConfigureAwait(false);
                await Click(A, 1_500, token).ConfigureAwait(false);
                // Reset TimeForRollBackCheck
                TimeForRollBackCheck = DateTime.Now;
            }

            var timing = config.Timings;
            var loadPro = timing.RestartGameSettings.ProfileSelectSettings.ProfileSelectionRequired ? timing.RestartGameSettings.ProfileSelectSettings.ExtraTimeLoadProfile : 0;

            await Click(A, 1_000 + loadPro, token).ConfigureAwait(false); // Initial "A" Press to start the Game + a delay if needed for profiles to load

            // Really Shouldn't keep this but we will for now
            if (timing.RestartGameSettings.AvoidSystemUpdate)
            {
                await Task.Delay(0_500, token).ConfigureAwait(false); // Delay bc why not
                await Click(DUP, 0_600, token).ConfigureAwait(false); // Highlight "Start Software"
                await Click(A, 1_000 + loadPro, token).ConfigureAwait(false); // Select "Sttart Software" + delay if Profile selection is needed
            }

            // Only send extra Presses if we need to
            if (timing.RestartGameSettings.ProfileSelectSettings.ProfileSelectionRequired)
            {
                await Click(A, 1_000, token).ConfigureAwait(false); // Now we are on the Profile Screen
                await Click(A, 1_000, token).ConfigureAwait(false); // Select the profile
            }

            // Digital game copies take longer to load
            if (timing.RestartGameSettings.CheckGameDelay)
            {
                await Task.Delay(2_000 + timing.RestartGameSettings.ExtraTimeCheckGame, token).ConfigureAwait(false);
            }

            // If they have DLC on the system and can't use it, requires an UP + A to start the game.
            if (timing.RestartGameSettings.CheckForDLC)
            {
                await Click(DUP, 0_600, token).ConfigureAwait(false);
                await Click(A, 0_600, token).ConfigureAwait(false);
            }

            Log("Restarting the game!");

            await Task.Delay(19_000 + timing.RestartGameSettings.ExtraTimeLoadGame, token).ConfigureAwait(false); // Wait for the game to load before writing to memory

            if (Settings.ActiveRaids.Count > 1)
            {
                Log($"Rotation for {Settings.ActiveRaids[RotationCount].Species} has been found.");
                Log($"Checking Current Game Progress Level.");

                var desiredProgress = (GameProgress)Settings.ActiveRaids[RotationCount].StoryProgressLevel;
                if (GameProgress != desiredProgress)
                {
                    Log($"Updating game progress level to: {desiredProgress}");
                    await WriteProgressLive(desiredProgress).ConfigureAwait(false);
                    GameProgress = desiredProgress;
                    Log($"Done.");
                }
                else
                {
                    Log($"Game progress level is already {GameProgress}. No update needed.");
                }

                if (Settings.RaidSettings.DisableOverworldSpawns)
                {
                    Log("Checking current state of Overworld Spawns.");
                    if (currentSpawnsEnabled.HasValue)
                    {
                        Log($"Current Overworld Spawns state: {currentSpawnsEnabled.Value}");

                        if (currentSpawnsEnabled.Value)
                        {
                            Log("Overworld Spawns are enabled, attempting to disable.");
                            await WriteBlock(false, RaidDataBlocks.KWildSpawnsEnabled, token, currentSpawnsEnabled);
                            currentSpawnsEnabled = false;
                            Log("Overworld Spawns successfully disabled.");
                        }
                        else
                        {
                            Log("Overworld Spawns are already disabled, no action taken.");
                        }
                    }
                }
                else // When Settings.DisableOverworldSpawns is false, ensure Overworld spawns are enabled
                {
                    Log("Settings indicate Overworld Spawns should be enabled. Checking current state.");
                    Log($"Current Overworld Spawns state: {currentSpawnsEnabled.Value}");

                    if (!currentSpawnsEnabled.Value)
                    {
                        Log("Overworld Spawns are disabled, attempting to enable.");
                        await WriteBlock(true, RaidDataBlocks.KWildSpawnsEnabled, token, currentSpawnsEnabled);
                        currentSpawnsEnabled = true;
                        Log("Overworld Spawns successfully enabled.");
                    }
                    else
                    {
                        Log("Overworld Spawns are already enabled, no action needed.");
                    }
                }

                Log($"Attempting to override seed for {Settings.ActiveRaids[RotationCount].Species}.");
                await OverrideSeedIndex(SeedIndexToReplace, token).ConfigureAwait(false);
                Log("Seed override completed.");
                if (Settings.RaidSettings.MysteryRaids && !firstRun)
                {
                    // Count the number of existing Mystery Shiny Raids
                    int mysteryRaidCount = Settings.ActiveRaids.Count(raid => raid.Title.Contains("Mystery Shiny Raid"));

                    // Only create and add a new Mystery Shiny Raid if there are two or fewer in the list
                    if (mysteryRaidCount <= 1)
                    {
                        CreateAndAddRandomShinyRaidAsRequested();
                    }
                }
            }

            for (int i = 0; i < 8; i++)
                await Click(A, 1_000, token).ConfigureAwait(false);

            var timer = 60_000;
            while (!await IsOnOverworldTitle(token).ConfigureAwait(false))
            {
                await Task.Delay(1_000, token).ConfigureAwait(false);
                timer -= 1_000;
                if (timer <= 0 && !timing.RestartGameSettings.AvoidSystemUpdate)
                {
                    Log("Still not in the game, initiating rescue protocol!");
                    while (!await IsOnOverworldTitle(token).ConfigureAwait(false))
                        await Click(A, 6_000, token).ConfigureAwait(false);
                    break;
                }
            }

            await Task.Delay(5_000 + timing.ExtraTimeLoadOverworld, token).ConfigureAwait(false);
            Log("Back in the overworld!");

            LostRaid = 0;
        }

        private async Task WriteProgressLive(GameProgress progress)
        {
            if (Connection is null)
                return;

            if (progress >= GameProgress.Unlocked3Stars)
            {
                var toexpect = (bool?)await ReadBlock(RaidDataBlocks.KUnlockedRaidDifficulty3, CancellationToken.None);
                await WriteBlock(true, RaidDataBlocks.KUnlockedRaidDifficulty3, CancellationToken.None, toexpect);
            }
            else
            {
                var toexpect = (bool?)await ReadBlock(RaidDataBlocks.KUnlockedRaidDifficulty3, CancellationToken.None);
                await WriteBlock(false, RaidDataBlocks.KUnlockedRaidDifficulty3, CancellationToken.None, toexpect);
            }

            if (progress >= GameProgress.Unlocked4Stars)
            {
                var toexpect = (bool?)await ReadBlock(RaidDataBlocks.KUnlockedRaidDifficulty4, CancellationToken.None);
                await WriteBlock(true, RaidDataBlocks.KUnlockedRaidDifficulty4, CancellationToken.None, toexpect);
            }
            else
            {
                var toexpect = (bool?)await ReadBlock(RaidDataBlocks.KUnlockedRaidDifficulty4, CancellationToken.None);
                await WriteBlock(false, RaidDataBlocks.KUnlockedRaidDifficulty4, CancellationToken.None, toexpect);
            }

            if (progress >= GameProgress.Unlocked5Stars)
            {
                var toexpect = (bool?)await ReadBlock(RaidDataBlocks.KUnlockedRaidDifficulty5, CancellationToken.None);
                await WriteBlock(true, RaidDataBlocks.KUnlockedRaidDifficulty5, CancellationToken.None, toexpect);
            }
            else
            {
                var toexpect = (bool?)await ReadBlock(RaidDataBlocks.KUnlockedRaidDifficulty5, CancellationToken.None);
                await WriteBlock(false, RaidDataBlocks.KUnlockedRaidDifficulty5, CancellationToken.None, toexpect);
            }

            if (progress >= GameProgress.Unlocked6Stars)
            {
                var toexpect = (bool?)await ReadBlock(RaidDataBlocks.KUnlockedRaidDifficulty6, CancellationToken.None);
                await WriteBlock(true, RaidDataBlocks.KUnlockedRaidDifficulty6, CancellationToken.None, toexpect);
            }
            else
            {
                var toexpect = (bool?)await ReadBlock(RaidDataBlocks.KUnlockedRaidDifficulty6, CancellationToken.None);
                await WriteBlock(false, RaidDataBlocks.KUnlockedRaidDifficulty6, CancellationToken.None, toexpect);
            }
        }

        private async Task SkipRaidOnLosses(CancellationToken token)
        {
            Log($"We had {Settings.LobbyOptions.SkipRaidLimit} lost/empty raids.. Moving on!");

            await SanitizeRotationCount(token).ConfigureAwait(false);
            // Prepare and send an embed to inform users
            await EnqueueEmbed(null, "", false, false, true, false, token).ConfigureAwait(false);
            await CloseGame(Hub.Config, token).ConfigureAwait(false);
            await StartGameRaid(Hub.Config, token).ConfigureAwait(false);
        }

        private static string AltPokeImg(PKM pkm)
        {
            string pkmform = string.Empty;
            if (pkm.Form != 0)
                pkmform = $"-{pkm.Form}";

            return _ = $"https://raw.githubusercontent.com/zyro670/PokeTextures/main/Placeholder_Sprites/scaled_up_sprites/Shiny/AlternateArt/" + $"{pkm.Species}{pkmform}" + ".png";
        }

        private async Task ReadRaids(bool init, CancellationToken token)
        {
            Log("Getting Raid data...");
            await InitializeRaidBlockPointers(init, token);

            string game = await DetermineGame(token);
            container = new(game);
            container.SetGame(game);

            await SetStoryAndEventProgress(token);

            var dataP = Array.Empty<byte>();
            var dataK = Array.Empty<byte>();
            var dataB = Array.Empty<byte>();

            if (ShouldReadPaldeaRaids(init))
            {
                dataP = await ReadPaldeaRaids(token);
                Log("Reading Paldea Raids...");
                await ProcessPaldeaRaids(dataP, token);
            }

            if (ShouldReadKitakamiRaids(init))
            {
                dataK = await ReadKitakamiRaids(token);
                Log("Reading Kitakami Raids...");
                await ProcessKitakamiRaids(dataK, token);
            }

            
            if (ShouldReadBlueberryRaids(init))
            {
                dataB = await ReadBlueberryRaids(token);
                Log("Reading Blueberry Raids...");
                await ProcessBlueberryRaids(dataB, token);
            }
            
            if (init)
            {
                await LocateSeedIndexOnInit(token);
            }

            await ProcessAllRaids(token);
        }

        private async Task InitializeRaidBlockPointers(bool init, CancellationToken token)
        {
            if (init || ShouldReadPaldeaRaids(init))
            {
                if (RaidBlockPointerP == 0)
                    RaidBlockPointerP = await SwitchConnection.PointerAll(Offsets.RaidBlockPointerP, token).ConfigureAwait(false);
            }

            if (init || ShouldReadKitakamiRaids(init))
            {
                if (RaidBlockPointerK == 0)
                    RaidBlockPointerK = await SwitchConnection.PointerAll(Offsets.RaidBlockPointerK, token).ConfigureAwait(false);
            }
            
            if (init || ShouldReadBlueberryRaids(init))
            {
                if (RaidBlockPointerB == 0)
                    RaidBlockPointerB = await SwitchConnection.PointerAll(Offsets.RaidBlockPointerB, token).ConfigureAwait(false);
            } 
            
        }

        private async Task<string> DetermineGame(CancellationToken token)
        {
            string id = await SwitchConnection.GetTitleID(token).ConfigureAwait(false);
            return id switch
            {
                RaidCrawler.Core.Structures.Offsets.ScarletID => "Scarlet",
                RaidCrawler.Core.Structures.Offsets.VioletID => "Violet",
                _ => "",
            };
        }

        private async Task SetStoryAndEventProgress(CancellationToken token)
        {
            var BaseBlockKeyPointer = await SwitchConnection.PointerAll(Offsets.BlockKeyPointer, token).ConfigureAwait(false);
            StoryProgress = await GetStoryProgress(BaseBlockKeyPointer, token).ConfigureAwait(false);
            EventProgress = Math.Min(StoryProgress, 3);
            await ReadEventRaids(BaseBlockKeyPointer, container, token).ConfigureAwait(false);
        }

        private bool ShouldReadPaldeaRaids(bool init)
        {
            return init || (SeedIndexToReplace >= 0 && SeedIndexToReplace < 69);
        }

        private async Task<byte[]> ReadPaldeaRaids(CancellationToken token)
        {
            return await SwitchConnection.ReadBytesAbsoluteAsync(RaidBlockPointerP + RaidBlock.HEADER_SIZE, (int)RaidBlock.SIZE_BASE, token).ConfigureAwait(false);
        }

        private bool ShouldReadKitakamiRaids(bool init)
        {
            return init || (SeedIndexToReplace >= 69 && SeedIndexToReplace < 94);
        }

        private async Task<byte[]> ReadKitakamiRaids(CancellationToken token)
        {
            return await SwitchConnection.ReadBytesAbsoluteAsync(RaidBlockPointerK, (int)RaidBlock.SIZE_KITAKAMI, token).ConfigureAwait(false);
        }

        private bool ShouldReadBlueberryRaids(bool init)
        {
            return init || SeedIndexToReplace >= 94;
        }

        private async Task<byte[]> ReadBlueberryRaids(CancellationToken token)
        {
            return await SwitchConnection.ReadBytesAbsoluteAsync(RaidBlockPointerB, (int)RaidBlock.SIZE_BLUEBERRY, token).ConfigureAwait(false);
        }

        private async Task LocateSeedIndexOnInit(CancellationToken token)
        {
            for (int rc = 0; rc < Settings.ActiveRaids.Count; rc++)
            {
                uint targetSeed = uint.Parse(Settings.ActiveRaids[rc].Seed, NumberStyles.AllowHexSpecifier);

                for (int i = 0; i < container.Raids.Count; i++)
                {
                    if (container.Raids[i].Seed == targetSeed)
                    {
                        // Skip updating for Might or Distribution raids
                        if (Settings.ActiveRaids[rc].CrystalType == TeraCrystalType.Might || Settings.ActiveRaids[rc].CrystalType == TeraCrystalType.Distribution)
                            continue;

                        RotationCount = rc;

                        // Determine the raid type and adjust the index accordingly
                        string raidType;
                        int adjustedIndex;
                        if (ShouldReadBlueberryRaids(true))
                        {
                            raidType = "Blueberry";
                            adjustedIndex = i + 94; // Blueberry starts at index 94
                        }
                        else if (ShouldReadKitakamiRaids(true))
                        {
                            raidType = "Kitakami";
                            adjustedIndex = i + 69; // Kitakami starts at index 69
                        }
                        else
                        {
                            raidType = "Paldea";
                            adjustedIndex = i; // Paldea starts at index 0
                        }
                        SeedIndexToReplace = adjustedIndex;
                        Log($"Raid Den Located at {adjustedIndex + 1:00} in {raidType} map");
                        Log($"Rotation Count set to {RotationCount}");
                        return;
                    }
                }
            }
        }

        private async Task ProcessPaldeaRaids(byte[] dataP, CancellationToken token)
        {
            int delivery, enc;

            (delivery, enc) = container.ReadAllRaids(dataP, StoryProgress, EventProgress, 0, TeraRaidMapParent.Paldea);

            if (enc > 0)
            {
                Log($"Failed to find encounters for {enc} Event raid.");
            }

            if (delivery > 0)
            {
                Log($"Invalid delivery group ID for {delivery} raid(s). Try deleting the \"cache\" folder.");
            }

            GameProgress currentProgress = (GameProgress)StoryProgress;
            if (currentProgress == GameProgress.Unlocked5Stars || currentProgress == GameProgress.Unlocked6Stars)
            {
                bool eventRaidFoundP = false;
                int eventRaidPAreaId = -1;
                int eventRaidPDenId = -1;
                int raidIndex = 0;

                foreach (var raid in container.Raids)
                {
                    if (raid.IsEvent)
                    {
                        eventRaidFoundP = true;
                        if (Settings.EventSettings.EventsOn)  // Check if EventOn is true
                        {
                            Settings.EventSettings.EventActive = true;  // Update EventActive only if EventOn is true
                            DisableMysteryRaidsIfEventActive();
                        }
                        eventRaidPAreaId = (int)raid.Area;
                        eventRaidPDenId = (int)raid.Den;

                        var areaText = $"{Areas.GetArea((int)(raid.Area - 1), raid.MapParent)} - Den {raid.Den}";
                        Log($"Event Raid found! Located in {areaText}");

                       /* if (raidIndex < num4List.Count)
                        {
                            Settings.EventSettings.RaidDeliveryGroupID = num4List[raidIndex];
                            Log($"Updating Delivery Group ID to {num4List[raidIndex]}.");
                        } */

                        break;
                    }
                    raidIndex++;
                }

                if (!eventRaidFoundP)
                {
                    Settings.EventSettings.RaidDeliveryGroupID = -1;
                    if (Settings.EventSettings.EventsOn)  // Check if EventOn is true
                    {
                        Settings.EventSettings.EventActive = false;  // Update EventActive only if EventOn is true
                    }
                }
            }
        }

        private async Task ProcessKitakamiRaids(byte[] dataK, CancellationToken token)
        {
            int delivery, enc;

            // Read all raids in the Kitakami region
            (delivery, enc) = container.ReadAllRaids(dataK, StoryProgress, EventProgress, 0, TeraRaidMapParent.Kitakami);

            // Log any failed encounters
            if (enc > 0)
            {
                Log($"Failed to find encounters for {enc} raid(s).");
            }

            // Log any invalid delivery group IDs
            if (delivery > 0)
            {
                Log($"Invalid delivery group ID for {delivery} raid(s). Try deleting the \"cache\" folder.");
            }
        }
        
        private async Task ProcessBlueberryRaids(byte[] dataB, CancellationToken token)
        {
            int delivery, enc;

            // Read all raids in the Blueberry region
            (delivery, enc) = container.ReadAllRaids(dataB, StoryProgress, EventProgress, 0, TeraRaidMapParent.Blueberry);

            // Log any failed encounters
            if (enc > 0)
            {
                Log($"Failed to find encounters for {enc} raid(s).");
            }

            // Log any invalid delivery group IDs
            if (delivery > 0)
            {
                Log($"Invalid delivery group ID for {delivery} raid(s). Try deleting the \"cache\" folder.");
            }
        }
        
        private async Task ProcessAllRaids(CancellationToken token)
        {
            var allRaids = container.Raids;
            var allEncounters = container.Encounters;
            var allRewards = container.Rewards;

            bool done = false;
            int raid_delivery_group_id = Settings.EventSettings.RaidDeliveryGroupID;

            for (int i = 0; i < allRaids.Count; i++)
            {
                if (done)
                    break;

                var (pk, seed) = IsSeedReturned(allEncounters[i], allRaids[i]);

                for (int a = 0; a < Settings.ActiveRaids.Count; a++)
                {
                    if (done)
                        break;

                    uint set;
                    try
                    {
                        set = uint.Parse(Settings.ActiveRaids[a].Seed, NumberStyles.AllowHexSpecifier);
                    }
                    catch (FormatException)
                    {
                        Log($"Invalid seed format detected. Removing {Settings.ActiveRaids[a].Seed} from list.");
                        Settings.ActiveRaids.RemoveAt(a);
                        a--;  // Decrement the index so that it does not skip the next element.
                        continue;  // Skip to the next iteration.
                    }

                    if (seed == set)
                    {
                        var res = GetSpecialRewards(allRewards[i], Settings.EmbedToggles.RewardsToShow);
                        RaidEmbedInfo.SpecialRewards = res;
                        if (string.IsNullOrEmpty(res))
                            res = string.Empty;
                        else
                            res = "**Special Rewards:**\n" + res;

                        var areaText = $"{Areas.GetArea((int)(allRaids[i].Area - 1), allRaids[i].MapParent)} - Den {allRaids[i].Den}";
                        Log($"Seed {seed:X8} found for {(Species)allEncounters[i].Species} in {areaText}");
                        Settings.ActiveRaids[a].Seed = $"{seed:X8}";
                        firstRun = false;
                        var stars = allRaids[i].IsEvent ? allEncounters[i].Stars : allRaids[i].GetStarCount(allRaids[i].Difficulty, StoryProgress, allRaids[i].IsBlack);
                        var encounter = allRaids[i].GetTeraEncounter(container, allRaids[i].IsEvent ? 3 : StoryProgress, raid_delivery_group_id);
                        var pkinfo = RaidExtensions<PK9>.GetRaidPrintName(pk);
                        var strings = GameInfo.GetStrings(1);
                        var moves = new ushort[4] { allEncounters[i].Move1, allEncounters[i].Move2, allEncounters[i].Move3, allEncounters[i].Move4 };
                        var movestr = string.Concat(moves.Where(z => z != 0).Select(z => $"{strings.Move[z]}ㅤ{Environment.NewLine}")).TrimEnd(Environment.NewLine.ToCharArray());
                        var extramoves = string.Empty;

                        if (allEncounters[i].ExtraMoves.Length != 0)
                        {
                            var extraMovesList = allEncounters[i].ExtraMoves.Where(z => z != 0).Select(z => $"{strings.Move[z]}\n");
                            extramoves = string.Concat(extraMovesList.Take(extraMovesList.Count()));
                            RaidEmbedInfo.ExtraMoves = extramoves;
                        }

                        var titlePrefix = allRaids[i].IsShiny ? "Shiny" : "";
                        RaidEmbedInfo.RaidSpecies = (Species)allEncounters[i].Species;
                        RaidEmbedInfo.RaidEmbedTitle = $"{stars} ★ {titlePrefix} {(Species)allEncounters[i].Species}{pkinfo}";
                        RaidEmbedInfo.RaidSpeciesGender = $"{(pk.Gender == 0 ? "Male" : pk.Gender == 1 ? "Female" : "Genderless")}";
                        RaidEmbedInfo.RaidSpeciesNature = GameInfo.Strings.Natures[pk.Nature];
                        RaidEmbedInfo.RaidSpeciesAbility = $"{(Ability)pk.Ability}";
                        RaidEmbedInfo.RaidSpeciesIVs = $"{pk.IV_HP}/{pk.IV_ATK}/{pk.IV_DEF}/{pk.IV_SPA}/{pk.IV_SPD}/{pk.IV_SPE}";
                        RaidEmbedInfo.RaidSpeciesTeraType = $"{(MoveType)allRaids[i].GetTeraType(encounter)}";
                        RaidEmbedInfo.Moves = string.Concat(moves.Where(z => z != 0).Select(z => $"{strings.Move[z]}\n")).TrimEnd(Environment.NewLine.ToCharArray());
                        RaidEmbedInfo.ScaleText = $"{PokeSizeDetailedUtil.GetSizeRating(pk.Scale)}";
                        RaidEmbedInfo.ScaleNumber = pk.Scale;
                        // Update Species and SpeciesForm in ActiveRaids
                        Settings.ActiveRaids[a].Species = (Species)allEncounters[i].Species;
                        Settings.ActiveRaids[a].SpeciesForm = allEncounters[i].Form;
                        done = true;
                    }
                }
            }
        }

        public static (PK9, Embed) RaidInfoCommand(string seedValue, int contentType, TeraRaidMapParent map, int storyProgressLevel, int raidDeliveryGroupID, List<string> rewardsToShow, bool isEvent = false)
        {
            byte[] enabled = StringToByteArray("00000001");
            byte[] area = StringToByteArray("00000001");
            byte[] displaytype = StringToByteArray("00000001");
            byte[] spawnpoint = StringToByteArray("00000001");
            byte[] thisseed = StringToByteArray(seedValue);
            byte[] unused = StringToByteArray("00000000");
            byte[] content = StringToByteArray($"0000000{contentType}"); // change this to 1 for 6-Star, 2 for 1-6 Star Events, 3 for Mighty 7-Star Raids
            byte[] leaguepoints = StringToByteArray("00000000");
            byte[] raidbyte = enabled.Concat(area).ToArray().Concat(displaytype).ToArray().Concat(spawnpoint).ToArray().Concat(thisseed).ToArray().Concat(unused).ToArray().Concat(content).ToArray().Concat(leaguepoints).ToArray();

            storyProgressLevel = storyProgressLevel switch
            {
                3 => 1,
                4 => 2,
                5 => 3,
                6 => 4,
                0 => 0,
                _ => 4 // default 6Unlocked
            };

            var raid = new Raid(raidbyte, map); // map is -> TeraRaidMapParent.Paldea, .Kitakami, or .Blueberry
            var progress = storyProgressLevel;
            var raid_delivery_group_id = raidDeliveryGroupID;
            var encounter = raid.GetTeraEncounter(container, raid.IsEvent ? 3 : progress, contentType == 3 ? 1 : raid_delivery_group_id);
            var reward = encounter.GetRewards(container, raid, 0);
            var stars = raid.IsEvent ? encounter.Stars : raid.GetStarCount(raid.Difficulty, storyProgressLevel, raid.IsBlack);
            var teraType = raid.GetTeraType(encounter);
            var form = encounter.Form;

            var param = encounter.GetParam();
            var pk = new PK9
            {
                Species = encounter.Species,
                Form = encounter.Form,
                Move1 = encounter.Move1,
                Move2 = encounter.Move2,
                Move3 = encounter.Move3,
                Move4 = encounter.Move4,
            };
            if (raid.IsShiny) pk.SetIsShiny(true);
            Encounter9RNG.GenerateData(pk, param, EncounterCriteria.Unrestricted, raid.Seed);
            var strings = GameInfo.GetStrings(1);
            var movesList = "";
            for (int i = 0; i < pk.Moves.Length; i++)
            {
                if (pk.Moves[i] != 0)
                {
                    movesList += $"\\- {strings.Move[pk.Moves[i]]}\n";
                }
            }
            var extraMoves = "";
            for (int i = 0; i < encounter.ExtraMoves.Length; i++)
            {
                if (encounter.ExtraMoves[i] != 0)
                {
                    extraMoves += $"\\- {strings.Move[encounter.ExtraMoves[i]]}\n";
                }
            }
            if (!string.IsNullOrEmpty(extraMoves)) movesList += $"**Extra Moves:**\n{extraMoves}";
            var specialRewards = GetSpecialRewards(reward, rewardsToShow);
            var teraTypeLower = strings.Types[teraType].ToLower();
            var teraIconUrl = $"https://genpkm.com/images/teraicons/icon1/{teraTypeLower}.png";
            var disclaimer = $"NotRaidBot {NotRaidBot.Version} by Gengar & Kai\nhttps://notpaldea.net";
            var titlePrefix = raid.IsShiny ? "Shiny " : "";
            var formName = ShowdownParsing.GetStringFromForm(pk.Form, strings, pk.Species, pk.Context);
            var authorName = $"{stars} ★ {titlePrefix}{(Species)encounter.Species}{(pk.Form != 0 ? $"-{formName}" : "")}{(isEvent ? " (Event Raid)" : "")}";

            (int R, int G, int B) = RaidExtensions<PK9>.GetDominantColor(RaidExtensions<PK9>.PokeImg(pk, false, false));
            var embedColor = new Color(R, G, B);

            var embed = new EmbedBuilder
            {
                Color = embedColor,
                ThumbnailUrl = RaidExtensions<PK9>.PokeImg(pk, false, false),
            };
            embed.AddField(x =>
            {
                x.Name = "**__Stats__**";
                x.Value = $"{Format.Bold($"TeraType:")} {strings.Types[teraType]} \n" +
                          $"{Format.Bold($"Ability:")} {strings.Ability[pk.Ability]}\n" +
                          $"{Format.Bold("Nature:")} {(Nature)pk.Nature}\n" +
                          $"{Format.Bold("IVs:")} {pk.IV_HP}/{pk.IV_ATK}/{pk.IV_DEF}/{pk.IV_SPA}/{pk.IV_SPD}/{pk.IV_SPE}\n" +
                          $"{Format.Bold($"Scale:")} {PokeSizeDetailedUtil.GetSizeRating(pk.Scale)}";
                x.IsInline = true;
            });

            embed.AddField("**__Moves__**", movesList, true);
            embed.AddField("**__Special Rewards__**", string.IsNullOrEmpty(specialRewards) ? "No Rewards To Display" : specialRewards, true);

            var programIconUrl = "https://genpkm.com/images/icon4.png";
            embed.WithFooter(new EmbedFooterBuilder()
            {
                Text = $"" + disclaimer,
                IconUrl = programIconUrl
            });

            embed.WithAuthor(auth =>
            {
                auth.Name = authorName;
                auth.IconUrl = teraIconUrl;
            });

            return (pk, embed.Build());
        }

        public static byte[] StringToByteArray(string hex)
        {
            int NumberChars = hex.Length;
            byte[] bytes = new byte[NumberChars / 2];
            for (int i = 0; i < NumberChars; i += 2)
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            Array.Reverse(bytes);
            return bytes;
        }

        private async Task<bool> PrepareForDayroll(CancellationToken token)
        {
            // Make sure we're connected.
            while (!await IsConnectedOnline(ConnectedOffset, token).ConfigureAwait(false))
            {
                Log("Connecting...");
                await RecoverToOverworld(token).ConfigureAwait(false);
                if (!await ConnectToOnline(Hub.Config, token).ConfigureAwait(false))
                    return false;
            }
            return true;
        }

        public async Task<bool> CheckForLobby(CancellationToken token)
        {
            var x = 0;
            Log("Connecting to lobby...");
            while (!await IsConnectedToLobby(token).ConfigureAwait(false))
            {
                await Click(A, 1_000, token).ConfigureAwait(false);
                x++;
                if (x == 15)
                {
                    Log("No den here! Rolling again.");
                    return false;
                }
            }
            return true;
        }

        private async Task<bool> SaveGame(PokeRaidHubConfig config, CancellationToken token)
        {

            await Click(X, 3_000, token).ConfigureAwait(false);
            await Click(R, 3_000 + config.Timings.ExtraTimeConnectOnline, token).ConfigureAwait(false);
            await Click(A, 3_000, token).ConfigureAwait(false);
            await Click(A, 1_000, token).ConfigureAwait(false);
            await Click(B, 1_000, token).ConfigureAwait(false);
            return true;
        }

        public class RaidEmbedInfo
        {
            public static string RaidEmbedTitle = string.Empty;
            public static Species RaidSpecies = Species.None;
            public static string RaidSpeciesGender = string.Empty;
            public static string RaidSpeciesIVs = string.Empty;
            public static string RaidSpeciesAbility = string.Empty;
            public static string RaidSpeciesNature = string.Empty;
            public static string RaidSpeciesTeraType = string.Empty;
            public static string Moves = string.Empty;
            public static string ExtraMoves = string.Empty;
            public static string ScaleText = string.Empty;
            public static string SpecialRewards = string.Empty;
            public static int ScaleNumber;
        }
    }
}