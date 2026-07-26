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
        public frmSaveAlert()
        {
            InitializeComponent();
        }

        // Este metodo lo genera Visual Studio automaticamente al hacer doble clic
        // en tu boton dentro del editor visual. Solo agrega la linea Close().
        private void btnOK_Click(object sender, EventArgs e)
        {
            // IMPORTANTE: solo cierra ESTA ventana, no el proceso completo.
            // El watcher (TeklaWatcher) sigue corriendo en la bandeja del sistema.
            Close();
        }
    }
}
