using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GP_models.Models
{
    public partial class ConvertionRecord : ObservableObject
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [ObservableProperty]
        private int _id;

        [ObservableProperty]
        private long _pixelsAmount;

        [ObservableProperty]
        private string? _gpuModel;

        [ObservableProperty]
        private int _cudaCores;

        [ObservableProperty]
        private double _convertionTime;

        [ObservableProperty]
        private string? _convertionDate;
    }
}
