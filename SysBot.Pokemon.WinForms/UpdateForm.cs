using System;
using System.Windows.Forms;
using System.Diagnostics;

public class UpdateForm : Form
{
    private Button buttonDownload;
    private Label labelUpdateInfo;

    public UpdateForm()
    {
        InitializeComponent();
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
        buttonDownload.Location = new System.Drawing.Point(75, 60);
        buttonDownload.Size = new System.Drawing.Size(130, 23);
        buttonDownload.Text = "Download Update";
        buttonDownload.Click += ButtonDownload_Click;

        // UpdateForm
        this.ClientSize = new System.Drawing.Size(284, 100);
        this.Controls.Add(this.labelUpdateInfo);
        this.Controls.Add(this.buttonDownload);
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.Name = "UpdateForm";
        this.StartPosition = FormStartPosition.CenterScreen;
        this.Text = "Update Available";
    }

    private void ButtonDownload_Click(object sender, EventArgs e)
    {
        // Open the download URL in the user's default browser
        Process.Start("http://genpkm.com/nrb/notraidbot.exe");
    }
}

