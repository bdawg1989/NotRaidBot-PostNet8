using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace SysBot.Pokemon.SV.BotRaid.Helpers
{
    public static class NotRaidBot
    {
        public const string Version = "v7.2";
        public const string Attribution = "https://notpaldea.net";
        public const string ConfigPath = "config.json";

        public static string GetBotPrefixFromJsonConfig()
        {
            try
            {
                // Read the file and parse the JSON
                var jsonData = File.ReadAllText(ConfigPath);
                var config = JObject.Parse(jsonData);

                // Access the CommandPrefix from the Discord settings
                var prefix = config["Discord"]["CommandPrefix"].ToString();
                return prefix;
            }
            catch (Exception ex)
            {
                // Handle any errors that occur during reading or parsing the file
                Console.WriteLine($"Error reading config file: {ex.Message}");
                return "!"; // Default prefix if error occurs
            }
        }
    }
}
