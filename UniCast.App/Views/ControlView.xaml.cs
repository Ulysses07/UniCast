using System;
using System.Windows;
using UniCast.App.Infrastructure;
using UniCast.App.ViewModels;
using Serilog;

namespace UniCast.App.Views
{
    public partial class ControlView : System.Windows.Controls.UserControl
    {
        private readonly ControlViewModel _vm;

        public ControlView() : this(new ControlViewModel())
        {
        }

        public ControlView(ControlViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;

            Loaded += ControlView_Loaded;
            Unloaded += ControlView_Unloaded;
        }

        // DÜZELTME v20: AsyncVoidHandler ile güvenli async event handler
        private void ControlView_Loaded(object sender, RoutedEventArgs e)
        {
            AsyncVoidHandler.Handle(
                async () => await _vm.StartPreviewAsync(),
                showErrorDialog: false);
        }

        // DÜZELTME v20: AsyncVoidHandler ile güvenli async event handler
        private void ControlView_Unloaded(object sender, RoutedEventArgs e)
        {
            AsyncVoidHandler.Handle(
                async () => await _vm.StopPreviewAsync(),
                showErrorDialog: false);
        }
    }
}