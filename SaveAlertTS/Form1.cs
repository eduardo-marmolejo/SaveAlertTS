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
            ApplyLanguage();
        }

        private void ApplyLanguage()
        {
            Text = Localization.FormTitle;
            lblMessage.Text = Localization.Message;
            lblMessage2.Text = Localization.SubMessage;
            btnOK.Text = Localization.OkSave;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            CenterControls();
        }

        private void CenterControls()
        {
            lblMessage.Left = (ClientSize.Width - lblMessage.Width) / 2;
            lblMessage2.Left = (ClientSize.Width - lblMessage2.Width) / 2;
            btnOK.Left = (ClientSize.Width - btnOK.Width) / 2;
        }

        public void SetTarget(uint pid, string modelPath)
        {
            TargetPid = pid;
            TargetModelPath = modelPath;
        }

        private async void btnOK_Click(object sender, EventArgs e)
        {
            btnOK.Enabled = false;
            btnOK.Text = Localization.Saving;
            lblMessage2.Text = Localization.SavingModel;
            CenterControls();

            bool saved = await Task.Run(() => TeklaSaveService.SaveCurrentModel(TargetPid, TargetModelPath));

            if (saved)
            {
                btnOK.Enabled = true;
                Close();
            }
            else
            {
                lblMessage.Text = Localization.SaveError;
                btnOK.Enabled = true;
                btnOK.Text = Localization.Retry;
                CenterControls();
            }
        }
    }
}
