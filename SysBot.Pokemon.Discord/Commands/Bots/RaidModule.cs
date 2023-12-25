using Discord;
using Discord.Commands;
using PKHeX.Core;
using SysBot.Base;
using SysBot.Pokemon.Discord.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static SysBot.Pokemon.RotatingRaidSettingsSV;
using static SysBot.Pokemon.SV.BotRaid.RotatingRaidBotSV;

namespace SysBot.Pokemon.Discord.Commands.Bots
{
    [Summary("Generates and queues various silly trade additions")]
    public class RaidModule<T> : ModuleBase<SocketCommandContext> where T : PKM, new()
    {
        private readonly PokeRaidHub<T> Hub = SysCord<T>.Runner.Hub;

        [Command("raidinfo")]
        [Alias("ri", "rv")]
        [Summary("Displays basic Raid Info of the provided seed.")]
        public async Task RaidSeedInfoAsync(
            string seedValue,
            int level,
            int storyProgressLevel = 6,
            string? eventType = null)  // New optional parameter for specifying event type

        {
            uint seed;
            try
            {
                seed = uint.Parse(seedValue, NumberStyles.AllowHexSpecifier);
            }
            catch (FormatException)
            {
                await ReplyAsync("Invalid seed format. Please enter a valid seed.");
                return;
            }

            var settings = Hub.Config.RotatingRaidSV;  // Get RotatingRaidSV settings

            var crystalType = level switch
            {
                >= 1 and <= 5 => eventType == "Event" ? (TeraCrystalType)2 : (TeraCrystalType)0,
                6 => (TeraCrystalType)1,
                7 => (TeraCrystalType)3,
                _ => throw new ArgumentException("Invalid difficulty level.")
            };

            // Check if event type is specified but events are turned off
            if (eventType == "Event" && !settings.EventSettings.EventActive)
            {
                await ReplyAsync("Sorry, but the Event Setting is turned off at this time or there are no Events active.").ConfigureAwait(false);
                return;
            }

            try
            {
                var selectedMap = IsBlueberry ? TeraRaidMapParent.Blueberry : (IsKitakami ? TeraRaidMapParent.Kitakami : TeraRaidMapParent.Paldea);
                var rewardsToShow = settings.EmbedToggles.RewardsToShow;
                var raidDeliveryGroupID = settings.EventSettings.RaidDeliveryGroupID;
                var isEvent = eventType == "Event";
                var (_, embed) = RaidInfoCommand(seedValue, (int)crystalType, selectedMap, storyProgressLevel, raidDeliveryGroupID, rewardsToShow, isEvent);
                await ReplyAsync(embed: embed);
            }
            catch (Exception ex)
            {
                await ReplyAsync(ex.Message);
            }
        }

        [Command("banOT")]
        [Alias("ban")]
        [RequireSudo]
        [Summary("Bans a user with the specified OT from participating in raids.")]
        public async Task BanUserAsync(string ot)
        {
            // Load the player data from the file.
            var baseDirectory = AppContext.BaseDirectory;
            var storage = new PlayerDataStorage(baseDirectory);
            var playerData = storage.LoadPlayerData();

            // Filter out player data that matches the OT.
            var matchedPlayers = playerData.Where(pd => pd.Value.OT.Equals(ot, StringComparison.OrdinalIgnoreCase)).ToList();

            // Check if there are duplicates.
            if (matchedPlayers.Count > 1)
            {
                await ReplyAsync($"Multiple players with OT '{ot}' found. Ban skipped. Please review manually.");
                return;
            }

            // If no player is found, notify and return.
            if (matchedPlayers.Count == 0)
            {
                await ReplyAsync($"No player with OT '{ot}' found.");
                return;
            }

            // Get the player's NID to ban.
            var playerToBan = matchedPlayers.First();
            ulong nidToBan = playerToBan.Key;

            // Check if the NID is already in the ban list.
            if (Hub.Config.RotatingRaidSV.RaiderBanList.List.Any(x => x.ID == nidToBan))
            {
                await ReplyAsync($"Player with OT '{ot}' is already banned.");
                return;
            }

            Hub.Config.RotatingRaidSV.RaiderBanList.AddIfNew(new[] { GetReference(nidToBan, ot, "") });

            // Optionally, save the ban list to persist the changes
            // SaveBanListMethod(Hub.Config.RaiderBanList);

            // Notify the command issuer that the ban was successful.
            await ReplyAsync($"Player with OT '{ot}' has been banned.");
        }

        [Command("banNID")]
        [Alias("ban")]
        [RequireSudo]
        [Summary("Bans a user with the specified NID from participating in raids.")]
        public async Task BanUserAsync(ulong nid, [Remainder] string comment)
        {
            var ot = string.Empty;
            try
            {
                var baseDirectory = AppContext.BaseDirectory;
                var storage = new PlayerDataStorage(baseDirectory);
                var playerData = storage.LoadPlayerData();
                var matchedNID = playerData.Where(pd => pd.Key.Equals(nid));
                ot = matchedNID.First().Value.OT;
            }
            catch
            {
                ot = "Unknown";
            }

            Hub.Config.RotatingRaidSV.RaiderBanList.AddIfNew(new[] { GetReference(nid, ot, comment) });
            await ReplyAsync("Done.").ConfigureAwait(false);
        }

        [Command("repeek")]
        [Summary("Take and send a screenshot from the specified Switch.")]
        [RequireOwner]
        public async Task RePeek(string address)
        {
            var source = new CancellationTokenSource();
            var token = source.Token;

            var bot = SysCord<T>.Runner.GetBot(address);
            if (bot == null)
            {
                await ReplyAsync($"No bot found with the specified address ({address}).").ConfigureAwait(false);
                return;
            }

            var c = bot.Bot.Connection;
            c.Reset();
            var bytes = await c.PixelPeek(token).ConfigureAwait(false) ?? Array.Empty<byte>();
            MemoryStream ms = new(bytes);
            var img = "cap.jpg";
            var embed = new EmbedBuilder { ImageUrl = $"attachment://{img}", Color = Color.Purple }.WithFooter(new EmbedFooterBuilder { Text = $"Captured image from bot at address {address}." });
            await Context.Channel.SendFileAsync(ms, img, "", false, embed: embed.Build());
        }

        [Command("addRaidParams")]
        [Alias("arp")]
        [Summary("Adds new raid parameter.")]
        [RequireSudo]
        public async Task AddNewRaidParam(
            [Summary("Seed")] string seed,
            [Summary("Difficulty Level (1-7)")] int level,
            [Summary("Story Progress Level")] int storyProgressLevel = 6,
            [Summary("Event (Optional)")] string? eventType = null)  // New optional parameter for specifying event type
        {
            // Validate the seed for hexadecimal format
            if (seed.Length != 8 || !seed.All(c => "0123456789abcdefABCDEF".Contains(c)))
            {
                await ReplyAsync("Invalid seed format. Please enter a seed consisting of exactly 8 hexadecimal digits.").ConfigureAwait(false);
                return;
            }
            if (level < 1 || level > 7)  // Adjusted level range to 1-7
            {
                await ReplyAsync("Invalid raid level. Please enter a level between 1 and 7.").ConfigureAwait(false);  // Adjusted message to reflect new level range
                return;
            }

            // Convert StoryProgressLevel to GameProgress enum value
            var gameProgress = ConvertToGameProgress(storyProgressLevel);

            // Get the settings object from Hub
            var settings = Hub.Config.RotatingRaidSV;

            var crystalType = level switch
            {
                >= 1 and <= 5 => eventType == "Event" ? (TeraCrystalType)2 : (TeraCrystalType)0,
                6 => (TeraCrystalType)1,
                7 => (TeraCrystalType)3,
                _ => throw new ArgumentException("Invalid difficulty level.")
            };

            // Check if event type is specified but events are turned off
            if (eventType == "Event" && !settings.EventSettings.EventActive)
            {
                await ReplyAsync("Sorry, but the Event Setting is turned off at this time or there are no Events active.").ConfigureAwait(false);
                return;
            }

            var selectedMap = IsBlueberry ? TeraRaidMapParent.Blueberry : (IsKitakami ? TeraRaidMapParent.Kitakami : TeraRaidMapParent.Paldea);
            var raidDeliveryGroupID = settings.EventSettings.RaidDeliveryGroupID;
            var rewardsToShow = settings.EmbedToggles.RewardsToShow;
            var (pk, raidEmbed) = RaidInfoCommand(seed, (int)crystalType, selectedMap, storyProgressLevel, raidDeliveryGroupID, rewardsToShow);
            var description = string.Empty;
            var prevpath = "bodyparam.txt";
            var filepath = "RaidFilesSV\\bodyparam.txt";
            if (File.Exists(prevpath))
                Directory.Move(filepath, prevpath + Path.GetFileName(filepath));

            if (File.Exists(filepath))
                description = File.ReadAllText(filepath);

            var data = string.Empty;
            var prevpk = "pkparam.txt";
            var pkpath = "RaidFilesSV\\pkparam.txt";
            if (File.Exists(prevpk))
                Directory.Move(pkpath, prevpk + Path.GetFileName(pkpath));

            if (File.Exists(pkpath))
                data = File.ReadAllText(pkpath);

            RotatingRaidParameters newparam = new()
            {
                CrystalType = crystalType,
                DifficultyLevel = level,
                Description = new[] { description },
                PartyPK = new[] { "" },
                Species = (Species)pk.Species,
                SpeciesForm = pk.Form,
                StoryProgressLevel = (int)gameProgress,
                Seed = seed,
                IsCoded = true,
                IsShiny = pk.IsShiny,
                AddedByRACommand = false,
                Title = $"{(Species)pk.Species}",
            };
            Hub.Config.RotatingRaidSV.ActiveRaids.Add(newparam);
            await Context.Message.DeleteAsync().ConfigureAwait(false);
            var msg = $"Your new raid has been added.";
            await ReplyAsync(msg, embed: raidEmbed).ConfigureAwait(false);
        }

        [Command("addUserRaid")]
        [Alias("aur", "ra")]
        [Summary("Adds new raid parameter next in the queue.")]
        public async Task AddNewRaidParamNext(
            [Summary("Seed")] string seed,
            [Summary("Difficulty Level (1-7)")] int level,
            [Summary("Story Progress Level")] int storyProgressLevel = 6,
            [Summary("Event (Optional)")] string? eventType = null)  // New argument for specifying an event
        {
            // Check if raid requests are disabled by the host
            if (Hub.Config.RotatingRaidSV.RaidSettings.DisableRequests)
            {
                await ReplyAsync("Raid Requests are currently disabled by the host.").ConfigureAwait(false);
                return;
            }

            // Check if the user already has a request
            var userId = Context.User.Id;
            if (Hub.Config.RotatingRaidSV.ActiveRaids.Any(r => r.RequestedByUserID == userId))
            {
                await ReplyAsync("You already have an existing raid request in the queue.").ConfigureAwait(false);
                return;
            }

            // Validate the seed for hexadecimal format
            if (seed.Length != 8 || !seed.All(c => "0123456789abcdefABCDEF".Contains(c)))
            {
                await ReplyAsync("Invalid seed format. Please enter a seed consisting of exactly 8 hexadecimal digits.").ConfigureAwait(false);
                return;
            }

            if (level < 1 || level > 7)  // Adjusted level range to 1-7
            {
                await ReplyAsync("Invalid raid level. Please enter a level between 1 and 7.").ConfigureAwait(false);  // Adjusted message to reflect new level range
                return;
            }

            // Convert StoryProgressLevel to GameProgress enum value
            var gameProgress = ConvertToGameProgress(storyProgressLevel);
            if (gameProgress == GameProgress.None)
            {
                await ReplyAsync("Invalid Story Progress Level. Please enter a value between 1 and 6.").ConfigureAwait(false);
                return;
            }

            // Get the settings object from Hub
            var settings = Hub.Config.RotatingRaidSV;

            var crystalType = level switch
            {
                >= 1 and <= 5 => eventType == "Event" ? (TeraCrystalType)2 : (TeraCrystalType)0,
                6 => (TeraCrystalType)1,
                7 => (TeraCrystalType)3,
                _ => throw new ArgumentException("Invalid difficulty level.")
            };

            // Check if event type is specified but events are turned off
            if (eventType == "Event" && !settings.EventSettings.EventActive)
            {
                await ReplyAsync("Sorry, but the Event Setting is turned off at this time or there are no Events active.").ConfigureAwait(false);
                return;
            }

            // If EventActive is true, force storyProgressLevel to 6
            if (settings.EventSettings.EventActive && storyProgressLevel != 6)
            {
                await ReplyAsync("Currently only Story Progress Level 6 (6* Unlocked) is allowed due to Active Event Settings.").ConfigureAwait(false);
                return;
            }
            var selectedMap = IsBlueberry ? TeraRaidMapParent.Blueberry : (IsKitakami ? TeraRaidMapParent.Kitakami : TeraRaidMapParent.Paldea);
            var rewardsToShow = settings.EmbedToggles.RewardsToShow;
            var raidDeliveryGroupID = settings.EventSettings.RaidDeliveryGroupID;
            var (pk, raidEmbed) = RaidInfoCommand(seed, (int)crystalType, selectedMap, storyProgressLevel, raidDeliveryGroupID, rewardsToShow);
            var description = string.Empty;
            var prevpath = "bodyparam.txt";
            var filepath = "RaidFilesSV\\bodyparam.txt";
            if (File.Exists(prevpath))
                Directory.Move(filepath, prevpath + Path.GetFileName(filepath));

            if (File.Exists(filepath))
                description = File.ReadAllText(filepath);

            var data = string.Empty;
            var prevpk = "pkparam.txt";
            var pkpath = "RaidFilesSV\\pkparam.txt";
            if (File.Exists(prevpk))
                Directory.Move(pkpath, prevpk + Path.GetFileName(pkpath));

            if (File.Exists(pkpath))
                data = File.ReadAllText(pkpath);

            RotatingRaidParameters newparam = new()
            {
                CrystalType = crystalType,
                Description = new[] { description },
                PartyPK = new[] { "" },
                Species = (Species)pk.Species,
                DifficultyLevel = level,
                SpeciesForm = pk.Form,
                StoryProgressLevel = (int)gameProgress,
                Seed = seed,
                IsCoded = true,
                IsShiny = pk.IsShiny,
                AddedByRACommand = true,
                RequestedByUserID = Context.User.Id,
                Title = $"{Context.User.Username}'s Requested Raid{(eventType == "Event" ? " (Event Raid)" : "")}",

                User = Context.User,
            };

            // Determine the correct position to insert the new raid after the current rotation
            int insertPosition = RotationCount + 1;
            while (insertPosition < Hub.Config.RotatingRaidSV.ActiveRaids.Count && Hub.Config.RotatingRaidSV.ActiveRaids[insertPosition].AddedByRACommand)
            {
                insertPosition++;
            }

            Hub.Config.RotatingRaidSV.ActiveRaids.Insert(insertPosition, newparam);

            await Context.Message.DeleteAsync().ConfigureAwait(false);
            var msg = $"{Context.User.Mention}, added your raid to the queue! I'll DM you when it's about to start.";
            await ReplyAsync(msg, embed: raidEmbed).ConfigureAwait(false);
        }

        public GameProgress ConvertToGameProgress(int storyProgressLevel)
        {
            return storyProgressLevel switch
            {
                6 => GameProgress.Unlocked6Stars,
                5 => GameProgress.Unlocked5Stars,
                4 => GameProgress.Unlocked4Stars,
                3 => GameProgress.Unlocked3Stars,
                2 => GameProgress.UnlockedTeraRaids,
                1 => GameProgress.UnlockedTeraRaids,
                _ => GameProgress.Unlocked6Stars
            };
        }

        [Command("addRaidPK")]
        [Alias("rp")]
        [Summary("Adds provided showdown set Pokémon to the users Raid in Queue.")]
        public async Task AddRaidPK([Summary("Showdown Set")][Remainder] string content)
        {
            content = ReusableActions.StripCodeBlock(content);
            var set = new ShowdownSet(content);
            var template = AutoLegalityWrapper.GetTemplate(set);
            if (set.InvalidLines.Count != 0 || set.Species <= 0)
            {
                var msg = $"Unable to parse Showdown Set:\n{string.Join("\n", set.InvalidLines)}";
                await ReplyAsync(msg).ConfigureAwait(false);
                return;
            }

            try
            {
                var sav = AutoLegalityWrapper.GetTrainerInfo<T>();
                var pkm = sav.GetLegal(template, out var result);
                var la = new LegalityAnalysis(pkm);
                var spec = GameInfo.Strings.Species[template.Species];
                pkm = EntityConverter.ConvertToType(pkm, typeof(T), out _) ?? pkm;
                if (pkm is not T pk || !la.Valid)
                {
                    var reason = result == "Timeout" ? $"That {spec} set took too long to generate." : $"I wasn't able to create a {spec} from that set.";
                    var imsg = $"Oops! {reason}";
                    await ReplyAsync(imsg).ConfigureAwait(false);
                    return;
                }

                var userId = Context.User.Id;
                var raidParameters = Hub.Config.RotatingRaidSV.ActiveRaids;
                var raidToUpdate = raidParameters.FirstOrDefault(r => r.RequestedByUserID == userId);
                string[] partyPK = content.Split('\n', StringSplitOptions.RemoveEmptyEntries); // Remove empty lines
                if (raidToUpdate != null)
                {
                    raidToUpdate.PartyPK = partyPK;
                    await Context.Message.DeleteAsync().ConfigureAwait(false);
                    var embed = RPEmbed.PokeEmbed(pkm, Context.User.Username);
                    await ReplyAsync(embed: embed).ConfigureAwait(false);
                }
                else
                {
                    var msg = "You don't have a raid in queue!";
                    await ReplyAsync(msg).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                LogUtil.LogSafe(ex, nameof(RaidModule<T>));
                var msg = $"Oops! An unexpected problem happened with this Showdown Set:\n```{string.Join("\n", set.GetSetLines())}```";
                await ReplyAsync(msg).ConfigureAwait(false);
            }
        }

        [Command("addRaidPK")]
        [Alias("rp")]
        [Summary("Adds provided showdown set Pokémon to the users Raid in Queue.")]
        public async Task AddRaidPK()
        {
            var attachment = Context.Message.Attachments.FirstOrDefault();
            if (attachment == default)
            {
                await ReplyAsync("No attachment provided!").ConfigureAwait(false);
                return;
            }

            var att = await NetUtil.DownloadPKMAsync(attachment).ConfigureAwait(false);
            var pk = GetRequest(att);
            if (pk == null)
            {
                await ReplyAsync("Attachment provided is not compatible with this module!").ConfigureAwait(false);
                return;
            }
            else
            {
                var userId = Context.User.Id;
                var raidParameters = Hub.Config.RotatingRaidSV.ActiveRaids;
                var raidToUpdate = raidParameters.FirstOrDefault(r => r.RequestedByUserID == userId);
                var set = ShowdownParsing.GetShowdownText(pk);
                string[] partyPK = set.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (raidToUpdate != null)
                {
                    raidToUpdate.PartyPK = partyPK;
                    await Context.Message.DeleteAsync().ConfigureAwait(false);
                    var embed = RPEmbed.PokeEmbed(pk, Context.User.Username);
                    await ReplyAsync(embed: embed).ConfigureAwait(false);
                }
                else
                {
                    var msg = "You don't have a raid in queue!";
                    await ReplyAsync(msg).ConfigureAwait(false);
                }
            }
        }

        [Command("raidQueueStatus")]
        [Alias("rqs")]
        [Summary("Checks the number of raids before the user's request and gives an ETA.")]
        public async Task CheckQueueStatus()
        {
            var userId = Context.User.Id;
            int currentPosition = RotationCount;

            var userRequestIndex = Hub.Config.RotatingRaidSV.ActiveRaids.FindIndex(r =>
                r.RequestedByUserID == userId && !r.Title.Contains("Mystery Shiny Raid"));

            EmbedBuilder embed = new EmbedBuilder();

            if (userRequestIndex == -1)
            {
                embed.Title = "Queue Status";
                embed.Color = Color.Red;
                embed.Description = $"{Context.User.Mention}, you do not have a raid request in the queue.";
            }
            else
            {
                int raidsBeforeUser;
                if (Hub.Config.RotatingRaidSV.RaidSettings.RandomRotation)
                {
                    raidsBeforeUser = CalculateEffectiveQueuePosition(userId, currentPosition);
                }
                else
                {
                    raidsBeforeUser = userRequestIndex - currentPosition;
                }

                if (raidsBeforeUser <= 0)
                {
                    embed.Title = "Queue Status";
                    embed.Color = Color.Green;
                    embed.Description = $"{Context.User.Mention}, your raid request is up next!";
                }
                else
                {
                    int etaMinutes = raidsBeforeUser * 6;
                    embed.Title = "Queue Status";
                    embed.Color = Color.Orange;
                    embed.Description = $"{Context.User.Mention}, here's the status of your raid request:";
                    embed.AddField("Raids Before Yours", raidsBeforeUser.ToString(), true);
                    embed.AddField("Estimated Time", $"{etaMinutes} minutes", true);
                }
            }

            embed.AddField("Users in Queue", BuildQueueList(currentPosition));

            await Context.Message.DeleteAsync().ConfigureAwait(false);
            await ReplyAsync(embed: embed.Build()).ConfigureAwait(false);
        }

        private int CalculateEffectiveQueuePosition(ulong userId, int currentPosition)
        {
            int effectivePosition = 0;

            for (int i = currentPosition; i < Hub.Config.RotatingRaidSV.ActiveRaids.Count; i++)
            {
                var raid = Hub.Config.RotatingRaidSV.ActiveRaids[i];

                if (raid.Title.Contains("Mystery Shiny Raid"))
                {
                    continue;
                }

                if (raid.RequestedByUserID == userId)
                {
                    break;
                }

                effectivePosition++;

                if (raid.AddedByRACommand)
                {
                    effectivePosition--;
                }
            }

            return effectivePosition;
        }

        private string BuildQueueList(int currentPosition)
        {
            StringBuilder queueList = new StringBuilder();

            for (int i = 0; i < Hub.Config.RotatingRaidSV.ActiveRaids.Count; i++)
            {
                var raid = Hub.Config.RotatingRaidSV.ActiveRaids[i];
                var user = raid.User;
                if (user != null && !raid.Title.Contains("Mystery Shiny Raid"))
                {
                    queueList.AppendLine($"{i + 1}: {user.Username} ({user.Id})");
                }
            }

            return queueList.Length > 0 ? queueList.ToString() : "No users in queue.";
        }

        [Command("raidQueueClear")]
        [Alias("rqc")]
        [Summary("Removes the raid added by the user.")]
        public async Task RemoveOwnRaidParam()
        {
            var userId = Context.User.Id;
            var list = Hub.Config.RotatingRaidSV.ActiveRaids;

            // Find the index of the user's raid
            int raidIndex = list.FindIndex(r => r.RequestedByUserID == userId && r.AddedByRACommand);
            if (raidIndex == -1)
            {
                await ReplyAsync("You don't have a raid added.").ConfigureAwait(false);
                return;
            }

            // Prevent canceling if the raid is next in line
            if (raidIndex == RotationCount)
            {
                await ReplyAsync("Your raid request is up next and cannot be canceled at this time.").ConfigureAwait(false);
                return;
            }

            // Proceed with removal if it's not next in line
            var raid = list[raidIndex];
            list.RemoveAt(raidIndex);

            // Normalize RotationCount if necessary
            if (RotationCount >= list.Count)
            {
                RotationCount = 0;
            }

            await Context.Message.DeleteAsync().ConfigureAwait(false);
            var msg = $"Cleared your Raid from the queue.";
            await ReplyAsync(msg).ConfigureAwait(false);
        }

        [Command("removeRaidParams")]
        [Alias("rrp")]
        [Summary("Removes a raid parameter.")]
        [RequireSudo]
        public async Task RemoveRaidParam([Summary("Seed Index")] int index)
        {
            var list = Hub.Config.RotatingRaidSV.ActiveRaids;
            if (index >= 0 && index < list.Count)
            {
                var raid = list[index];
                list.RemoveAt(index);
                var msg = $"Raid for {raid.Title} | {raid.Seed:X8} has been removed!";
                await ReplyAsync(msg).ConfigureAwait(false);
            }
            else
                await ReplyAsync("Invalid raid parameter index.").ConfigureAwait(false);
        }

        [Command("toggleRaidParams")]
        [Alias("trp")]
        [Summary("Toggles raid parameter.")]
        [RequireSudo]
        public async Task ToggleRaidParam([Summary("Seed Index")] int index)
        {
            var list = Hub.Config.RotatingRaidSV.ActiveRaids;
            if (index >= 0 && index < list.Count)
            {
                var raid = list[index];
                raid.ActiveInRotation = !raid.ActiveInRotation;
                var m = raid.ActiveInRotation ? "enabled" : "disabled";
                var msg = $"Raid for {raid.Title} | {raid.Seed:X8} has been {m}!";
                await ReplyAsync(msg).ConfigureAwait(false);
            }
            else
                await ReplyAsync("Invalid raid parameter Index.").ConfigureAwait(false);
        }

        [Command("togglecodeRaidParams")]
        [Alias("tcrp")]
        [Summary("Toggles code raid parameter.")]
        [RequireSudo]
        public async Task ToggleCodeRaidParam([Summary("Seed Index")] int index)
        {
            var list = Hub.Config.RotatingRaidSV.ActiveRaids;
            if (index >= 0 && index < list.Count)
            {
                var raid = list[index];
                raid.IsCoded = !raid.IsCoded;
                var m = raid.IsCoded ? "coded" : "uncoded";
                var msg = $"Raid for {raid.Title} | {raid.Seed:X8} is now {m}!";
                await ReplyAsync(msg).ConfigureAwait(false);
            }
            else
                await ReplyAsync("Invalid raid parameter Index.").ConfigureAwait(false);
        }

        [Command("changeRaidParamTitle")]
        [Alias("crpt")]
        [Summary("Changes the title of a  raid parameter.")]
        [RequireSudo]
        public async Task ChangeRaidParamTitle([Summary("Seed Index")] int index, [Summary("Title")] string title)
        {
            var list = Hub.Config.RotatingRaidSV.ActiveRaids;
            if (index >= 0 && index < list.Count)
            {
                var raid = list[index];
                raid.Title = title;
                var msg = $"Raid Title for {raid.Title} | {raid.Seed:X8} has been changed to: {title}!";
                await ReplyAsync(msg).ConfigureAwait(false);
            }
            else
                await ReplyAsync("Invalid raid parameter Index.").ConfigureAwait(false);
        }

        [Command("viewraidList")]
        [Alias("vrl", "rotatinglist")]
        [Summary("Prints the raids in the current collection.")]
        public async Task GetRaidListAsync()
        {
            var list = Hub.Config.RotatingRaidSV.ActiveRaids;
            int count = list.Count;
            int fields = (int)Math.Ceiling((double)count / 15);
            var embed = new EmbedBuilder
            {
                Title = "Raid List"
            };
            for (int i = 0; i < fields; i++)
            {
                int start = i * 15;
                int end = Math.Min(start + 14, count - 1);
                var fieldBuilder = new StringBuilder();
                for (int j = start; j <= end; j++)
                {
                    var raid = list[j];
                    int paramNumber = j;
                    fieldBuilder.AppendLine($"{paramNumber}.) {raid.Title} - {raid.Seed} - Status: {(raid.ActiveInRotation ? "Active" : "Inactive")}");
                }
                embed.AddField($"Raid List - Part {i + 1}", fieldBuilder.ToString(), false);
            }
            await ReplyAsync($"These are the raids currently in the list (total: {count}):", embed: embed.Build()).ConfigureAwait(false);
        }

        [Command("toggleRaidPK")]
        [Alias("trpk")]
        [Summary("Toggles raid parameter.")]
        [RequireSudo]
        public async Task ToggleRaidParamPK([Summary("Seed Index")] int index, [Summary("Showdown Set")][Remainder] string content)
        {
            var list = Hub.Config.RotatingRaidSV.ActiveRaids;
            if (index >= 0 && index < list.Count)
            {
                var raid = list[index];
                raid.PartyPK = new[] { content };
                var m = string.Join("\n", raid.PartyPK);
                var msg = $"RaidPK for {raid.Title} | {raid.Seed:X8} has been updated to:\n{m}!";
                await ReplyAsync(msg).ConfigureAwait(false);
            }
            else
                await ReplyAsync("Invalid raid parameter Index.").ConfigureAwait(false);
        }

        [Command("raidhelp")]
        [Alias("rh")]
        [Summary("Prints the raid help command list.")]
        public async Task GetRaidHelpListAsync()
        {
            var embed = new EmbedBuilder();
            List<string> cmds = new()
            {
                "$ban - Ban a user from raids via NID. [Command] [OT] - Sudo only command.\n",
                "$vrl - View all raids in the list.\n",
                "$arp - Add parameter to the collection.\nEx: [Command] [Index] [Species] [Difficulty]\n",
                "$rrp - Remove parameter from the collection.\nEx: [Command] [Index]\n",
                "$trp - Toggle the parameter as Active/Inactive in the collection.\nEx: [Command] [Index]\n",
                "$tcrp - Toggle the parameter as Coded/Uncoded in the collection.\nEx: [Command] [Index]\n",
                "$trpk - Set a PartyPK for the parameter via a showdown set.\nEx: [Command] [Index] [ShowdownSet]\n",
                "$crpt - Set the title for the parameter.\nEx: [Command] [Index]"
            };
            string msg = string.Join("", cmds.ToList());
            embed.AddField(x =>
            {
                x.Name = "Raid Help Commands";
                x.Value = msg;
                x.IsInline = false;
            });
            await ReplyAsync("Here's your raid help!", embed: embed.Build()).ConfigureAwait(false);
        }

        [Command("unbanrotatingraider")]
        [Alias("ubrr")]
        [Summary("Removes the specificed NID from the banlist for Raids in SV.")]
        [RequireSudo]
        public async Task UnbanRotatingRaider([Summary("Removes the specificed NID from the banlist for Raids in SV.")] string nid)
        {
            var list = Hub.Config.RotatingRaidSV.RaiderBanList.List.ToArray();
            string msg = $"{Context.User.Mention} no user found with that NID.";
            for (int i = 0; i < list.Length; i++)
                if ($"{list[i].ID}".Equals(nid))
                {
                    msg = $"{Context.User.Mention} user {list[i].Name} - {list[i].ID} has been unbanned.";
                    Hub.Config.RotatingRaidSV.RaiderBanList.List.ToList().Remove(list[i]);
                }
            await ReplyAsync(msg).ConfigureAwait(false);
        }

        private RemoteControlAccess GetReference(ulong id, string name, string comment) => new()
        {
            ID = id,
            Name = name,
            Comment = "Banned on " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC" + $"({comment})"
        };

        private static T? GetRequest(Download<PKM> dl)
        {
            if (!dl.Success)
                return null;
            return dl.Data switch
            {
                null => null,
                T pk => pk,
                _ => EntityConverter.ConvertToType(dl.Data, typeof(T), out _) as T,
            };
        }
    }
}