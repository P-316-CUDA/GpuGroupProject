using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GP_models.Models
{
    public class AppDbContext : DbContext
    {
        public DbSet<ConvertionRecord> ConvertionRecords { get; set; }

        public AppDbContext()
        {
            this.Database.EnsureCreated();
            if (!ConvertionRecords.Any())
            {
                ConvertionRecord record = new ConvertionRecord()
                {
                    PixelsAmount = 0,
                    GpuModel = string.Empty,
                    CudaCores = 0,
                    ConvertionTime = 0,
                    ConvertionDate = string.Empty,
                };
                ConvertionRecords.Add(record);
                this.SaveChanges();
            }
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlite("Data Source=../../../../Records.db");
        }

        public string ExportAsJson()
        {
            List<ConvertionRecord> records = this.ConvertionRecords.ToList();
            return JsonConvert.SerializeObject(records, Formatting.Indented);
        }
        public void ImportFromJson(string json)
        {
            try
            {
                List<ConvertionRecord> records = JsonConvert.DeserializeObject<List<ConvertionRecord>>(json);
                ConvertionRecords.AddRange(records);
            }
            catch
            {
                //Ошибка Invalid Json
            }
        }
    }
}
