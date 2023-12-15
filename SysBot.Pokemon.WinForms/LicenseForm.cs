// LicenseForm.cs
using System;
using System.Windows.Forms;

public class LicenseForm : Form
{
    private TextBox textBoxLicense;
    private Button buttonConfirm;
    private Button buttonCancel;
    private Label labelInfo;  // Declare the label

    public string LicenseKey { get; private set; }

    public LicenseForm()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        textBoxLicense = new TextBox();
        buttonConfirm = new Button();
        buttonCancel = new Button();
        labelInfo = new Label();  // Initialize the label

        // 
        // textBoxLicense
        // 
        textBoxLicense.Location = new System.Drawing.Point(50, 50);
        textBoxLicense.Name = "textBoxLicense";
        textBoxLicense.Size = new System.Drawing.Size(200, 20);

        // 
        // buttonConfirm
        // 
        buttonConfirm.Location = new System.Drawing.Point(50, 100);
        buttonConfirm.Name = "buttonConfirm";
        buttonConfirm.Size = new System.Drawing.Size(75, 23);
        buttonConfirm.Text = "Confirm";
        buttonConfirm.Click += ButtonConfirm_Click;

        // 
        // buttonCancel
        // 
        buttonCancel.Location = new System.Drawing.Point(175, 100);
        buttonCancel.Name = "buttonCancel";
        buttonCancel.Size = new System.Drawing.Size(75, 23);
        buttonCancel.Text = "Cancel";
        buttonCancel.Click += ButtonCancel_Click;

        // 
        // labelInfo
        // 
        labelInfo.Location = new System.Drawing.Point(50, 20);  // Adjusted position
        labelInfo.Name = "labelInfo";
        labelInfo.Size = new System.Drawing.Size(200, 20);
        labelInfo.Text = "Please enter your License Key";

        // 
        // LicenseForm
        // 
        this.ClientSize = new System.Drawing.Size(300, 200);
        this.Controls.Add(this.labelInfo);  // Add the label to the form
        this.Controls.Add(this.textBoxLicense);
        this.Controls.Add(this.buttonConfirm);
        this.Controls.Add(this.buttonCancel);
    }

    private void ButtonConfirm_Click(object sender, EventArgs e)
    {
        // Validate the license key here
        LicenseKey = textBoxLicense.Text;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ButtonCancel_Click(object sender, EventArgs e)
    {
        DialogResult = DialogResult.Cancel;
        Close();
    }
}
