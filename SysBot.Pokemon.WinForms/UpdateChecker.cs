using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using SysBot.Pokemon.SV.BotRaid.Helpers; // Add this to reference the NotRaidBot class

public class UpdateChecker
{
    private const string VersionUrl = "https://genpkm.com/nrb/version.txt";

    public async Task<bool> CheckForUpdatesAsync()
    {
        string latestVersion = await FetchLatestVersionAsync();
        if (!string.IsNullOrEmpty(latestVersion) && latestVersion != NotRaidBot.Version)
        {
            UpdateForm updateForm = new UpdateForm();
            updateForm.ShowDialog();
            return true; // Update is available
        }
        return false; // No update available
    }

    // Add a method to fetch the changelog
    public async Task<string> FetchChangelogAsync()
    {
        using (var client = new HttpClient())
        {
            try
            {
                string changelog = await client.GetStringAsync("https://genpkm.com/nrb/changelog.txt");
                return changelog.Trim();
            }
            catch (Exception)
            {
                // Handle exceptions (e.g., network errors)
                return "Changelog is not available at the moment.";
            }
        }
    }

    private async Task<string> FetchLatestVersionAsync()
    {
        using (var client = new HttpClient())
        {
            try
            {
                string latestVersion = await client.GetStringAsync(VersionUrl);
                return latestVersion.Trim();
            }
            catch (Exception)
            {
                // Handle exceptions (e.g., network errors)
                return null;
            }
        }
    }
}

