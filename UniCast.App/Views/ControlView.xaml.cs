using System;
using System.Windows;
using UniCast.App.ViewModels;
using Serilog;

namespace UniCast.App.Views
{
    public partial class ControlView : System.Windows.Controls.UserControl
    {
        private readonly ControlViewModel _vm;

        public ControlView(ControlViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;

            Loaded += ControlView_Loaded;
            Unloaded += ControlView_Unloaded;
        }

        // DÜZELTME: Try-catch ile exception handling
        private async void ControlView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await _vm.StartPreviewAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Preview başlatma hatası");
            }
        }

        // DÜZELTME: Try-catch ile exception handling
        private async void ControlView_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await _vm.StopPreviewAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Preview durdurma hatası");
            }
        }
    }
}