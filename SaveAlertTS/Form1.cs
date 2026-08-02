using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SaveAlertTS
{
    public partial class frmSaveAlert : Form
    {
        public uint TargetPid { get; private set; }
        public string TargetModelPath { get; private set; }

        public frmSaveAlert()
        {
            InitializeComponent();
        }

        public void SetTarget(uint pid, string modelPath)
        {
            TargetPid = pid;
            TargetModelPath = modelPath;
        }

        private async void btnOK_Click(object sender, EventArgs e)
        {
            btnOK.Enabled = false;
            btnOK.Text = "Saving...";
            lblMessage2.Text = "Saving the model...";

            bool saved = await Task.Run(() => TeklaSaveService.SaveCurrentModel(TargetPid, TargetModelPath));

            if (saved)
            {
                btnOK.Enabled = true;
                Close();
            }
            else
            {
                lblMessage.Text = "Error saving the model.";
                btnOK.Enabled = true;
                btnOK.Text = "Retry";
            }
        }
    }
}
