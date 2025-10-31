using System.Threading.Tasks;
using UniCast.App.ViewModels;

namespace UniCast.App.Views
{
    public partial class ControlView : System.Windows.Controls.UserControl
    {
        public ControlView(ControlViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;

            Loaded += async (_, __) => await vm.StartPreviewAsync();
            Unloaded += async (_, __) => await vm.StopPreviewAsync();
        }
    }
}
