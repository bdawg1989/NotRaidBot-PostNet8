using System;
using System.Windows.Forms;
using System.Diagnostics;
using SysBot.Pokemon.WinForms;
using System.Drawing;
using System.Threading.Tasks;

public class UpdateForm : Form
{
    private Button buttonDownload;
    private Label labelUpdateInfo;
    private TextBox textBoxChangelog;

    public UpdateForm()
    {
        InitializeComponent();
        Load += async (sender, e) => await FetchAndDisplayChangelog();
    }

    private void InitializeComponent()
    {
        labelUpdateInfo = new Label();
        buttonDownload = new Button();

        // labelUpdateInfo
        labelUpdateInfo.AutoSize = true;
        labelUpdateInfo.Location = new System.Drawing.Point(12, 9);
        labelUpdateInfo.Size = new System.Drawing.Size(260, 40);
        labelUpdateInfo.Text = "A new version is available. Please download the latest version.";

        // buttonDownload
        buttonDownload.Location = new System.Drawing.Point(110, 107);
        buttonDownload.Size = new System.Drawing.Size(130, 23);
        buttonDownload.Text = "Download Update";
        buttonDownload.Click += ButtonDownload_Click;

        // UpdateForm
        this.ClientSize = new System.Drawing.Size(350, 150);
        this.Controls.Add(this.labelUpdateInfo);
        this.Controls.Add(this.buttonDownload);
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.Name = "UpdateForm";
        this.StartPosition = FormStartPosition.CenterScreen;
        this.Text = "Update Available";

        textBoxChangelog = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Location = new Point(10, 50), // Adjust as needed
            Size = new Size(330, 80), // Adjust as needed
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom | AnchorStyles.Right
        };

        this.Controls.Add(this.textBoxChangelog);
    }
    private async Task FetchAndDisplayChangelog()
    {
        UpdateChecker updateChecker = new UpdateChecker();
        string changelog = await updateChecker.FetchChangelogAsync();
        textBoxChangelog.Text = changelog;
    }
    private void ButtonDownload_Click(object sender, EventArgs e)
    {
        Main.IsUpdating = true;
        // Correctly open the URL in the default web browser
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://genpkm.com/nrb/NotRaidBot.exe",
            UseShellExecute = true
        });
        MessageBox.Show("Please close this program and replace the program with the one that just downloaded.", "Update Instructions", MessageBoxButtons.OK, MessageBoxIcon.Information);
        Application.Exit();
    }

}

