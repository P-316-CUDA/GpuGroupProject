using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using BenchmarkDotNet.Running;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using GP_models.Models;
using GroupProject.Models;
using ILGPU.Runtime;
using Newtonsoft.Json;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;


namespace GroupProject.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        [ObservableProperty]
        private string gpuName;
        [ObservableProperty]
        private IImmutableSolidColorBrush gpuActiveColour;
        [ObservableProperty]
        private byte[]? image;
        [ObservableProperty]
        private int width;
        [ObservableProperty]
        private int height;
		[ObservableProperty]
        private Bitmap? previewImage;
        [ObservableProperty]
        private Bitmap? convertedImage;
        [ObservableProperty]
        private System.Drawing.Bitmap? previewBitmap;
        [ObservableProperty]
        private System.Drawing.Bitmap? convertedBitmap;
        [ObservableProperty]
        private bool canAddImage = true;
        [ObservableProperty]
        private bool canConvert = true;
        [ObservableProperty]
        private ILGPUImageEqualizer equalizer;

        public Interaction<FilePickerSaveOptions, IStorageFile?> ShowSaveDialog { get; } = new Interaction<FilePickerSaveOptions, IStorageFile?>();
        public Interaction<FilePickerOpenOptions, IReadOnlyList<IStorageFile>?> ShowOpenFileDialog { get; } = new Interaction<FilePickerOpenOptions, IReadOnlyList<IStorageFile>?>();

        //DataBase
        [ObservableProperty]
        private ObservableCollection<ConvertionRecord> convertionRecords;

        //Hitograms
        [ObservableProperty]
        int[] previewValues;
        [ObservableProperty]
        int[] convertedValues;


        public MainWindowViewModel()
        {
            equalizer = new ILGPUImageEqualizer();
            gpuName = equalizer.GPUName;
            if (gpuName == null) { GpuActiveColour = Brushes.Red; } else { GpuActiveColour = Brushes.LightGreen; };
			using (AppDbContext context = new AppDbContext())
            {
                convertionRecords = new ObservableCollection<ConvertionRecord>();
                convertionRecords.AddRange(context.ConvertionRecords.ToArray());
            }
        }

        [RelayCommand]
        private async Task UploadImage()
        {
            var files = await ShowOpenFileDialog.Handle(new FilePickerOpenOptions
            {
                Title = "Select an image",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                        new FilePickerFileType("Image files")
                        {
                            Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif" }
                        }
                }
            }).FirstAsync();

            if (files.Count == 0 || files[0] is not IStorageFile file)
                return;

            await using var stream = await file.OpenReadAsync();
            using var memoryStream = new MemoryStream();

            await stream.CopyToAsync(memoryStream);
            byte[] imageBytes = memoryStream.ToArray();

            Image = imageBytes;

            memoryStream.Seek(0, SeekOrigin.Begin);
            PreviewImage = Bitmap.DecodeToWidth(memoryStream, 400);
            PreviewBitmap = ConvertToSystemDrawingBitmap(PreviewImage);

            CanAddImage = false;
            width = PreviewBitmap.Width;
            height = PreviewBitmap.Height;
           
		}
        [RelayCommand]
        private async Task SaveConvertedImage()
        {
            if (ConvertedBitmap == null) return;
            var file = await ShowSaveDialog.Handle(new FilePickerSaveOptions
            {
                Title = "Save an image",
                DefaultExtension = "png",
            }).FirstAsync();
            if (file == null)
                return;
            await using(var stream = await file.OpenWriteAsync())
            {
                ConvertedBitmap.Save(stream, ImageFormat.Png);
            }
        }
        [RelayCommand]
        private void Clear()
        {
            Image = null;
            PreviewImage = null;
            previewBitmap = null;
            PreviewValues = null;
            ConvertedImage = null;
            ConvertedBitmap = null;
            ConvertedValues = null;

            CanAddImage = true;
            CanConvert = true;
        }
        [RelayCommand]
        private async Task ExportToJson()
        {
            try
            {
                var options = new FilePickerSaveOptions
                {
                    Title = "Сохранить результаты как JSON",
                    DefaultExtension = "json",
                    ShowOverwritePrompt = true,
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType("JSON файл") { Patterns = new[] { "*.json" } }
                    }
                };

                var file = await ShowSaveDialog.Handle(options);

                var records = ConvertionRecords.ToList();

                string json = JsonConvert.SerializeObject(records, Formatting.Indented);

                await using var stream = await file.OpenWriteAsync();
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(json);
                await writer.FlushAsync();

            }
            catch (Exception ex)
            {
                //
            }

        }
        [RelayCommand]
        private void ConvertImage()
        {

            if (previewImage == null) return;
			var stopwatch = Stopwatch.StartNew();
			PreviewValues = equalizer.GetHistogram(previewBitmap);
            ConvertedBitmap = equalizer.EqualizeBitmap(previewBitmap);
            ConvertedValues = equalizer.GetHistogram(ConvertedBitmap);
            ConvertedImage = ConvertSystemDrawingToAvaloniaBitmap(ConvertedBitmap);
            CanConvert = false;
			stopwatch.Stop();
			AddConvertionRecord(stopwatch.ElapsedMilliseconds);


		}
		private async Task AddConvertionRecord(double time)
		{
            var record = new ConvertionRecord
            {
                PixelsAmount = width * height,
                GpuModel = equalizer.GPUName,
                CudaCores = equalizer.GPUCudaCores,
                ConvertionTime = time,
                ConvertionDate = DateTime.Now.ToString(),
            };
            using(AppDbContext context = new AppDbContext())
            {
               await context.ConvertionRecords.AddAsync(record);
                context.SaveChanges();
                ConvertionRecords =  new ObservableCollection<ConvertionRecord>(context.ConvertionRecords.ToArray());
            }
		}

		public Bitmap ConvertSystemDrawingToAvaloniaBitmap(System.Drawing.Bitmap systemBitmap)
        {
            using (var memoryStream = new MemoryStream())
            {
                systemBitmap.Save(memoryStream, System.Drawing.Imaging.ImageFormat.Png);
                memoryStream.Position = 0;
                return new Bitmap(memoryStream);
            }
        }
        public System.Drawing.Bitmap ConvertToSystemDrawingBitmap(Bitmap avaloniaBitmap)
        {
            if (avaloniaBitmap == null)
            {
                return null;
            }

            using (MemoryStream memoryStream = new MemoryStream())
            {
                // Save the Avalonia bitmap to the memory stream. 
                // It is recommended to use Png format to preserve transparency and quality.
                avaloniaBitmap.Save(memoryStream);

                // Reset the stream position to the beginning before reading
                memoryStream.Position = 0;

                // Create a System.Drawing.Bitmap from the memory stream
                System.Drawing.Bitmap systemDrawingBitmap = new System.Drawing.Bitmap(memoryStream);

                return systemDrawingBitmap;
            }
        }
        public async Task<string?> GetFilePathAsync(IStorageFile file)
        {
            // Попробовать получить путь через безопасный метод
            var path = await file.SaveBookmarkAsync();
            if (!string.IsNullOrEmpty(path))
            {
                // Для Windows/Linux/Mac можно получить путь
                return path;
            }
            else return null;
        }
    }
}
