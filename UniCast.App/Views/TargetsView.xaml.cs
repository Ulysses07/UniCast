namespace UniCast.App.Views
{
    public partial class TargetsView : System.Windows.Controls.UserControl
    {
        public TargetsView()
        {
            InitializeComponent();
        }

        public TargetsView(UniCast.App.ViewModels.TargetsViewModel vm) : this()
        {
            DataContext = vm;
        }
    }
}
