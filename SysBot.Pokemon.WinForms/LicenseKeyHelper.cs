using Newtonsoft.Json.Linq;
using SysBot.Base;
using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

public static class LicenseKeyHelper
{
    private static readonly string licenseFilePath = "license.key";

    public static void SaveLicenseKey(string key)
    {
        File.WriteAllText(licenseFilePath, key);
    }
    public static void DeleteLicenseKey()
    {
        if (File.Exists(licenseFilePath))
            File.Delete(licenseFilePath);
    }

    public static string ReadLicenseKey()
    {
        if (File.Exists(licenseFilePath))
            return File.ReadAllText(licenseFilePath);
        else
            return null;
    }
    public static string GetCpuId()
    {
        string cpuId = string.Empty;
        ManagementClass managementClass = new ManagementClass("win32_processor");
        ManagementObjectCollection managementObjectCollection = managementClass.GetInstances();

        foreach (ManagementObject managementObject in managementObjectCollection)
        {
            cpuId = managementObject.Properties["processorID"].Value.ToString();
            break;
        }

        return cpuId;
    }
    public static async Task<bool> ValidateLicenseAsync(string licenseKey)
    {
        using (HttpClient httpClient = new HttpClient())
        {
            try
            {
                string cpuId = GetCpuId();

                var content = new FormUrlEncodedContent(new[]
                {
                new KeyValuePair<string, string>("license_key", licenseKey),
                new KeyValuePair<string, string>("cpu_id", cpuId)
            });

                var response = await httpClient.PostAsync("https://genpkm.com/rblicenses/validate_license.php", content);
                var responseString = await response.Content.ReadAsStringAsync();

                JObject jsonResponse = JObject.Parse(responseString);
                if ((string)jsonResponse["status"] == "success")
                {
                    return true;
                }
            }
            catch (HttpRequestException ex) when (ex.InnerException is WebException webEx && webEx.Status == WebExceptionStatus.TrustFailure)
            {
                // Handle SSL certificate errors here.
                LogUtil.LogError($"SSL Certificate error when validating license. Error: {ex.Message}", "ValidateLicenseAsync");

            }
            catch (Exception ex)
            {
                // Handle other errors here.
                LogUtil.LogError($"SSL Certificate error when validating license. Error: {ex.Message}", "ValidateLicenseAsync");

            }
        }

        return false;
    }

    public static async Task<string> ValidateLicenseAndFetchNameAsync(string licenseKey)
    {
        using (HttpClient httpClient = new HttpClient())
        {
            string cpuId = GetCpuId();
            var content = new FormUrlEncodedContent(new[]
            {
            new KeyValuePair<string, string>("license_key", licenseKey),
            new KeyValuePair<string, string>("cpu_id", cpuId)
        });

            var response = await httpClient.PostAsync("https://genpkm.com/rblicenses/validate_license.php", content);
            var responseString = await response.Content.ReadAsStringAsync();

            JObject jsonResponse = JObject.Parse(responseString);
            if ((string)jsonResponse["status"] == "success")
            {
                return (string)jsonResponse["discord_name"];
            }
        }
        return string.Empty;
    }
}
