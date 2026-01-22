using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using GP_models.Models;
using GroupProject.ViewModels;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static System.Net.Mime.MediaTypeNames;

namespace GroupProject
{
    public partial class MainWindow : Window
    {

        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainWindowViewModel();
            if (DataContext is MainWindowViewModel viewModel)
            {

                viewModel.ShowOpenFileDialog.RegisterHandler(async (context) =>
                {
                    var files = await StorageProvider.OpenFilePickerAsync(context.Input);
                    context.SetOutput(files);
                });
                viewModel.ShowSaveDialog.RegisterHandler(async (context) =>
                {
                    var files = await StorageProvider.SaveFilePickerAsync(context.Input);
                    context.SetOutput(files);
                });
            }
        }
    }
}