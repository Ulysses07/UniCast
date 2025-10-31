using UniCast.App.ViewModels;

namespace UniCast.App.Views
{
    public partial class PreviewView : System.Windows.Controls.UserControl
    {
        public PreviewView(PreviewViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
        }
    }
}
