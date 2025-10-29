using System;
using System.Windows.Controls;
using UniCast.App.ViewModels;

namespace UniCast.App.Views
{
    public partial class TargetsView : UserControl
    {
        public TargetsViewModel Vm { get; }
        public TargetsView(TargetsViewModel vm)
        {
            InitializeComponent();
            Vm = vm ?? throw new ArgumentNullException(nameof(vm));
            DataContext = Vm;
        }
    }
}
