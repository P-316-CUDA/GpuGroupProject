using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;
using Microsoft.Diagnostics.Tracing.Parsers.FrameworkEventSource;

namespace GroupProject.Models
{
    public class ILGPUImageEqualizer : IDisposable
    {
        private readonly Context _context;
		private readonly Accelerator _accelerator;
        private readonly System.Random _random = new System.Random();
        public string GPUName;
        public int GPUCudaCores;

        public ILGPUImageEqualizer()
        {
            _context = Context.CreateDefault();
            _accelerator = _context.CreateCudaAccelerator(0);
            GPUName = _accelerator.Name;
            if (_accelerator is CudaAccelerator cudaAccelerator)
            {
                GPUCudaCores = cudaAccelerator.NumMultiprocessors * GetCoresPerMultiprocessor(cudaAccelerator.Architecture);
            }
            else GPUCudaCores = 0;
        }


        // 1. Ядро для вычисления гистограммы
        private static void CalculateHistogramKernel(
            Index1D index,
            ArrayView<byte> image,
            ArrayView<int> histogram)
        {
            // Атомарное увеличение счетчика
            Index1D pixelValue = image[index]; // FIXED: используем byte вместо Index1D
            Atomic.Add(ref histogram[pixelValue], 1);
        }

        // 3. Ядро для применения LUT
        private static void ApplyLUTKernel(
            Index1D index,
            ArrayView<byte> input,
            ArrayView<byte> output,
            ArrayView<byte> lut)
        {
            Index1D pixelValue = input[index]; // FIXED: используем byte вместо Index1D
            output[index] = lut[pixelValue];
        }

        // 4. Ядро для цветового преобразования RGB → YCbCr
        private static void RGBToYCbCrKernel(
            Index1D index,
            ArrayView<byte> rgb,
            ArrayView<byte> ycbcr)
        {
            int pixelIdx = index;
            int baseIdx = pixelIdx * 3;

            if (baseIdx + 2 < rgb.Length)
            {
                byte r = rgb[baseIdx];
                byte g = rgb[baseIdx + 1];
                byte b = rgb[baseIdx + 2];

                // Формула RGB to YCbCr
                int y = (int)(0.299f * r + 0.587f * g + 0.114f * b);
                int cb = 128 + (int)(-0.168736f * r - 0.331264f * g + 0.5f * b);
                int cr = 128 + (int)(0.5f * r - 0.418688f * g - 0.081312f * b);

                ycbcr[baseIdx] = (byte)Math.Min(255, Math.Max(0, y));
                ycbcr[baseIdx + 1] = (byte)Math.Min(255, Math.Max(0, cb));
                ycbcr[baseIdx + 2] = (byte)Math.Min(255, Math.Max(0, cr));
            }
        }

        // 5. Ядро для преобразования YCbCr → RGB
        private static void YCbCrToRGBKernel(
            Index1D index,
            ArrayView<byte> ycbcr,
            ArrayView<byte> rgb)
        {
            int pixelIdx = index;
            int baseIdx = pixelIdx * 3;

            if (baseIdx + 2 < ycbcr.Length)
            {
                byte y = ycbcr[baseIdx];
                byte cb = ycbcr[baseIdx + 1];
                byte cr = ycbcr[baseIdx + 2];

                // Формула YCbCr to RGB
                int c = y - 16;
                int d = cb - 128;
                int e = cr - 128;

                int r = (int)(1.164f * c + 1.596f * e);
                int g = (int)(1.164f * c - 0.392f * d - 0.813f * e);
                int b = (int)(1.164f * c + 2.017f * d);

                rgb[baseIdx] = (byte)Math.Min(255, Math.Max(0, r));
                rgb[baseIdx + 1] = (byte)Math.Min(255, Math.Max(0, g));
                rgb[baseIdx + 2] = (byte)Math.Min(255, Math.Max(0, b));
            }
        }

        // 6. ОТДЕЛЬНЫЙ метод для получения гистограммы
        public int[] GetHistogram(byte[] imageData, int width, int height)
        {
            int imageSize = width * height;

            if (imageSize == 0 || imageData.Length != imageSize)
                throw new ArgumentException("Invalid image data");

            try
            {
                // Загружаем ядро для гистограммы
                var histogramKernel = _accelerator.LoadAutoGroupedStreamKernel<
                    Index1D, ArrayView<byte>, ArrayView<int>>(CalculateHistogramKernel);

                // Выделяем память на устройстве
                using var deviceInput = _accelerator.Allocate1D<byte>(imageSize);
                using var deviceHistogram = _accelerator.Allocate1D<int>(256);

                // Копируем входные данные
                deviceInput.CopyFromCPU(imageData);

                // Вычисляем гистограмму
                deviceHistogram.MemSetToZero();
                histogramKernel(imageSize, deviceInput.View, deviceHistogram.View);
                _accelerator.Synchronize();

                // Копируем результат
                return deviceHistogram.GetAsArray1D();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in GetHistogram: {ex.Message}");
                throw;
            }
        }

        // 7. Метод для эквализации grayscale с использованием гистограммы
        public byte[] EqualizeGrayscale(byte[] imageData, int width, int height)
        {
            int imageSize = width * height;

            if (imageSize == 0 || imageData.Length != imageSize)
                throw new ArgumentException("Invalid image data");

            try
            {
                // Получаем гистограмму
                int[] histogram = GetHistogram(imageData, width, height);

                // Вычисляем кумулятивную гистограмму на CPU
                int[] cumulative = new int[256];
                int sum = 0;
                for (int i = 0; i < 256; i++)
                {
                    sum += histogram[i];
                    cumulative[i] = sum;
                }

                // Создаем LUT на CPU
                byte[] lut = new byte[256];
                float scale = 255.0f / imageSize;
                for (int i = 0; i < 256; i++)
                {
                    lut[i] = (byte)Math.Min(255, (int)(cumulative[i] * scale));
                }

                // Загружаем ядро для применения LUT
                var applyLUTKernel = _accelerator.LoadAutoGroupedStreamKernel<
                    Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<byte>>(ApplyLUTKernel);

                // Выделяем память
                using var deviceInput = _accelerator.Allocate1D<byte>(imageData);
                using var deviceOutput = _accelerator.Allocate1D<byte>(imageSize);
                using var deviceLUT = _accelerator.Allocate1D<byte>(lut);

                // Применяем LUT
                applyLUTKernel(imageSize, deviceInput.View, deviceOutput.View, deviceLUT.View);
                _accelerator.Synchronize();

                // Копируем результат
                return deviceOutput.GetAsArray1D();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in EqualizeGrayscale: {ex.Message}");
                throw;
            }
        }

        // 8. Метод для цветовой эквализации (RGB)
        public byte[] EqualizeColorRGB(byte[] rgbData, int width, int height)
        {
            int imageSize = width * height;
            int dataSize = imageSize * 3;

            if (rgbData.Length < dataSize)
                throw new ArgumentException("Invalid RGB data size");

            try
            {
                // Загружаем ядра для цветовых преобразований
                var rgbToYcbcrKernel = _accelerator.LoadAutoGroupedStreamKernel<
                    Index1D, ArrayView<byte>, ArrayView<byte>>(RGBToYCbCrKernel);

                var ycbcrToRgbKernel = _accelerator.LoadAutoGroupedStreamKernel<
                    Index1D, ArrayView<byte>, ArrayView<byte>>(YCbCrToRGBKernel);

                // Выделяем память
                using var deviceRGB = _accelerator.Allocate1D<byte>(dataSize);
                using var deviceYCbCr = _accelerator.Allocate1D<byte>(dataSize);
                using var deviceResult = _accelerator.Allocate1D<byte>(dataSize);

                // 1. Копируем RGB данные
                deviceRGB.CopyFromCPU(rgbData);

                // 2. Конвертируем RGB → YCbCr
                rgbToYcbcrKernel(imageSize, deviceRGB.View, deviceYCbCr.View);
                _accelerator.Synchronize();

                // 3. Извлекаем Y канал
                byte[] ycbcrData = deviceYCbCr.GetAsArray1D();
                byte[] yChannel = new byte[imageSize];

                for (int i = 0; i < imageSize; i++)
                {
                    yChannel[i] = ycbcrData[i * 3];
                }

                // 4. Эквализируем Y канал
                byte[] equalizedY = EqualizeGrayscale(yChannel, width, height);

                // 5. Обновляем Y канал
                for (int i = 0; i < imageSize; i++)
                {
                    ycbcrData[i * 3] = equalizedY[i];
                }
                deviceYCbCr.CopyFromCPU(ycbcrData);

                // 6. Конвертируем обратно YCbCr → RGB
                ycbcrToRgbKernel(imageSize, deviceYCbCr.View, deviceResult.View);
                _accelerator.Synchronize();

                return deviceResult.GetAsArray1D();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in EqualizeColorRGB: {ex.Message}");
                throw;
            }
        }

        // 9. Метод для цветовой эквализации (RGBA)
        public byte[] EqualizeColorRGBA(byte[] rgbaData, int width, int height)
        {
            int imageSize = width * height;
            int rgbaSize = imageSize * 4;

            if (rgbaData.Length < rgbaSize)
                throw new ArgumentException("Invalid RGBA data size");

            // Извлекаем RGB (игнорируем альфа-канал для обработки)
            byte[] rgbData = new byte[imageSize * 3];
            for (int i = 0; i < imageSize; i++)
            {
                rgbData[i * 3] = rgbaData[i * 4];
                rgbData[i * 3 + 1] = rgbaData[i * 4 + 1];
                rgbData[i * 3 + 2] = rgbaData[i * 4 + 2];
            }

            // Обрабатываем RGB
            byte[] equalizedRGB = EqualizeColorRGB(rgbData, width, height);

            // Собираем обратно RGBA (сохраняем альфа-канал)
            byte[] result = new byte[rgbaSize];
            for (int i = 0; i < imageSize; i++)
            {
                result[i * 4] = equalizedRGB[i * 3];
                result[i * 4 + 1] = equalizedRGB[i * 3 + 1];
                result[i * 4 + 2] = equalizedRGB[i * 3 + 2];
                result[i * 4 + 3] = rgbaData[i * 4 + 3]; // Сохраняем альфа
            }

            return result;
        }

        // 10. Универсальный метод для эквализации (автоматически определяет формат)
        public byte[] EqualizeImage(byte[] imageData, int width, int height, int channels)
        {
            if (imageData == null)
                throw new ArgumentNullException(nameof(imageData));
            if (width <= 0 || height <= 0)
                throw new ArgumentException("Invalid image dimensions");
            if (channels < 1 || channels > 4)
                throw new ArgumentException("Channels must be 1, 3, or 4");

            Console.WriteLine($"Equalizing image: {width}x{height}, {channels} channels");

            return channels switch
            {
                1 => EqualizeGrayscale(imageData, width, height),
                3 => EqualizeColorRGB(imageData, width, height),
                4 => EqualizeColorRGBA(imageData, width, height),
                _ => throw new ArgumentException($"Unsupported channel count: {channels}")
            };
        }

        // 11. Метод для работы с Bitmap
        public Bitmap EqualizeBitmap(Bitmap bitmap)
        {
            try
            {
                Console.WriteLine($"Processing bitmap: {bitmap.Width}x{bitmap.Height}, Format: {bitmap.PixelFormat}");

                // Определяем количество каналов
                int channels;
                PixelFormat targetFormat;

                switch (bitmap.PixelFormat)
                {
                    case PixelFormat.Format8bppIndexed:
                    case PixelFormat.Format1bppIndexed:
                    case PixelFormat.Format4bppIndexed:
                        // Индексированные форматы конвертируем в RGB
                        channels = 3;
                        targetFormat = PixelFormat.Format24bppRgb;
                        break;

                    case PixelFormat.Format24bppRgb:
                        channels = 3;
                        targetFormat = PixelFormat.Format24bppRgb;
                        break;

                    case PixelFormat.Format32bppArgb:
                    case PixelFormat.Format32bppRgb:
                    case PixelFormat.Format32bppPArgb:
                        channels = 4;
                        targetFormat = PixelFormat.Format32bppArgb;
                        break;

                    default:
                        // Неподдерживаемые форматы конвертируем в RGB
                        Console.WriteLine($"Warning: Unsupported format {bitmap.PixelFormat}, converting to RGB");
                        channels = 3;
                        targetFormat = PixelFormat.Format24bppRgb;
                        break;
                }

                // Создаем копию в нужном формате
                Bitmap targetBitmap = new Bitmap(bitmap.Width, bitmap.Height, targetFormat);

                using (Graphics g = Graphics.FromImage(targetBitmap))
                    g.DrawImage(bitmap, 0, 0, bitmap.Width, bitmap.Height);

                // Обрабатываем
                BitmapData bmpData = targetBitmap.LockBits(
                    new Rectangle(0, 0, targetBitmap.Width, targetBitmap.Height),
                    ImageLockMode.ReadWrite,
                    targetFormat);

                try
                {
                    int stride = Math.Abs(bmpData.Stride);
                    int width = targetBitmap.Width;
                    int height = targetBitmap.Height;

                    // Вычисляем размеры
                    int bytesPerPixel = channels;
                    int unpaddedRowSize = width * bytesPerPixel;
                    int unpaddedTotalSize = unpaddedRowSize * height;
                    int totalSizeWithPadding = stride * height;

                    // Читаем данные с padding
                    byte[] imageData = new byte[totalSizeWithPadding];
                    Marshal.Copy(bmpData.Scan0, imageData, 0, totalSizeWithPadding);

                    // Убираем padding
                    byte[] unpaddedData = new byte[unpaddedTotalSize];
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.BlockCopy(imageData, y * stride,
                                       unpaddedData, y * unpaddedRowSize,
                                       unpaddedRowSize);
                    }

                    // Обрабатываем изображение
                    byte[] resultData = channels switch
                    {
                        3 => EqualizeColorRGB(unpaddedData, width, height),
                        4 => EqualizeColorRGBA(unpaddedData, width, height),
                        _ => throw new InvalidOperationException($"Unsupported channels: {channels}")
                    };

                    // Добавляем padding обратно
                    byte[] resultWithPadding = new byte[totalSizeWithPadding];
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.BlockCopy(resultData, y * unpaddedRowSize,
                                       resultWithPadding, y * stride,
                                       unpaddedRowSize);
                    }

                    // Записываем результат
                    Marshal.Copy(resultWithPadding, 0, bmpData.Scan0, totalSizeWithPadding);
                }
                finally
                {
                    targetBitmap.UnlockBits(bmpData);
                }

                // Освобождаем оригинал
                bitmap.Dispose();

                return targetBitmap;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in EqualizeBitmap: {ex.Message}");
                throw;
            }
        }

        public int[] GetHistogram(Bitmap bitmap)
        {
            if (bitmap == null)
                throw new ArgumentNullException(nameof(bitmap));

            // Конвертируем изображение в grayscale данные
            byte[] grayData = ConvertBitmapToGrayscaleData(bitmap);

            // Вычисляем гистограмму на GPU
            return ComputeHistogramOnGPU(grayData, bitmap.Width, bitmap.Height);
        }

        private byte[] ConvertBitmapToGrayscaleData(Bitmap bitmap)
        {
            // Создаем копию в формате, который можем обработать
            Bitmap processedBitmap;

            if (bitmap.PixelFormat == PixelFormat.Format8bppIndexed)
            {
                processedBitmap = (Bitmap)bitmap.Clone();
            }
            else
            {
                processedBitmap = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(processedBitmap))
                    g.DrawImage(bitmap, 0, 0);
            }

            // Получаем данные изображения
            BitmapData bmpData = processedBitmap.LockBits(
                new Rectangle(0, 0, processedBitmap.Width, processedBitmap.Height),
                ImageLockMode.ReadOnly,
                processedBitmap.PixelFormat);

            try
            {
                int width = processedBitmap.Width;
                int height = processedBitmap.Height;
                int stride = Math.Abs(bmpData.Stride);
                int imageSize = width * height;

                byte[] imageData = new byte[stride * height];
                Marshal.Copy(bmpData.Scan0, imageData, 0, imageData.Length);

                // Конвертируем в grayscale
                byte[] grayData = new byte[imageSize];

                if (processedBitmap.PixelFormat == PixelFormat.Format8bppIndexed)
                {
                    // 8bpp - данные уже в grayscale (по индексам палитры)
                    // Для простоты берем индексы как значения яркости
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.BlockCopy(imageData, y * stride, grayData, y * width, width);
                    }
                }
                else
                {
                    // RGB to grayscale
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int srcIndex = y * stride + x * 3;
                            int dstIndex = y * width + x;

                            // Формула для grayscale
                            byte r = imageData[srcIndex];
                            byte g = imageData[srcIndex + 1];
                            byte b = imageData[srcIndex + 2];

                            grayData[dstIndex] = (byte)(0.299 * r + 0.587 * g + 0.114 * b);
                        }
                    }
                }

                return grayData;
            }
            finally
            {
                processedBitmap.UnlockBits(bmpData);

                // Освобождаем временную копию
                if (processedBitmap != bitmap)
                    processedBitmap.Dispose();
            }
        }

        private int[] ComputeHistogramOnGPU(byte[] grayData, int width, int height)
        {
            int imageSize = width * height;

            if (grayData.Length != imageSize)
                throw new ArgumentException("Invalid grayscale data size");

            // Загружаем ядро для гистограммы
            var histogramKernel = _accelerator.LoadAutoGroupedStreamKernel<
                Index1D, ArrayView<byte>, ArrayView<int>>(CalculateHistogramKernel);

            // Выделяем память на GPU
            using var deviceInput = _accelerator.Allocate1D<byte>(imageSize);
            using var deviceHistogram = _accelerator.Allocate1D<int>(256);

            // Копируем данные на GPU
            deviceInput.CopyFromCPU(grayData);

            // Обнуляем гистограмму на GPU
            deviceHistogram.MemSetToZero();

            // Выполняем вычисление гистограммы на GPU
            histogramKernel(imageSize, deviceInput.View, deviceHistogram.View);
            _accelerator.Synchronize();

            // Копируем результат обратно на CPU
            return deviceHistogram.GetAsArray1D();
        }

        public void Dispose()
        {
            try
            {
                _accelerator?.Dispose();
                _context?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error disposing ILGPU: {ex.Message}");
            }

            GC.SuppressFinalize(this);
        }
        private int GetCoresPerMultiprocessor(CudaArchitecture arch)
        {
            // Основные версии Compute Capability
            return (arch.Major, arch.Minor) switch
            {
                // Fermi
                (2, 0) => 32,   // GF100
                (2, 1) => 48,   // GF10x

                // Kepler
                (3, 0) => 192,  // GK10x
                (3, 2) => 192,  // GK20x (Tegra)
                (3, 5) => 192,  // GK11x
                (3, 7) => 192,  // GK21x

                // Maxwell
                (5, 0) => 128,  // GM10x
                (5, 2) => 128,  // GM20x
                (5, 3) => 128,  // Tegra X1

                // Pascal
                (6, 0) => 64,   // GP100
                (6, 1) => 128,  // GP10x
                (6, 2) => 128,  // GP10b (Tegra)

                // Volta
                (7, 0) => 64,   // GV100
                (7, 2) => 64,   // GV10b (Tegra)

                // Turing
                (7, 5) => 64,   // TU10x

                // Ampere
                (8, 0) => 64,   // GA100
                (8, 6) => 128,  // GA10x
                (8, 7) => 128,  // GA10b (мобильные)
                (8, 9) => 128,  // GA10x (новые)

                // Ada Lovelace
                (9, 0) => 128,  // Следующее поколение

                // По умолчанию для неизвестных архитектур
                _ => arch.Major >= 7 ? 64 : 128
            };
        }
    }
}
