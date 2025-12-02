using UniCast.App.ViewModels;

namespace UniCast.App.Views
{
    public partial class TargetsView : System.Windows.Controls.UserControl
    {
        public TargetsView()
        {
            InitializeComponent();

            // ViewModel'i oluştur ve bağla
            DataContext = new TargetsViewModel();
        }

        public TargetsView(TargetsViewModel vm) : this()
        {
            DataContext = vm;
        }
    }
}