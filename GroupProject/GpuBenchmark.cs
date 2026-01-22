using Avalonia;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

using GroupProject.Models;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using BenchmarkDotNet.Running;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using GP_models.Models;
using GroupProject.Models;
using Newtonsoft.Json;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;


namespace GroupProject
{
	[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 5, invocationCount: 1)]
	public class GpuBenchmark
    {
		public static System.Drawing.Bitmap pbm;
		public static ILGPUImageEqualizer eq;
		public static System.Drawing.Bitmap cbm;
		public static int[] pValues;
		public static int[] cValues;
		[Benchmark]
		public void GpuTest()
		{
			Test();
		}
		private void Test()
		{
			pValues = eq.GetHistogram(pbm);
			cbm = eq.EqualizeBitmap(pbm);
			cValues = eq.GetHistogram(cbm);
		}
	}
}
