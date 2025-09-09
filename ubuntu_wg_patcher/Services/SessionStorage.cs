using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ubuntu_wg_patcher.Models;

namespace ubuntu_wg_patcher.Services
{
    public class SessionStorage
    {
        private readonly string _filePath;
        private readonly string _tempFilePath;

        public SessionStorage()
        {
            _filePath = Path.Combine(AppContext.BaseDirectory, "last_session.json");
            _tempFilePath = Path.Combine(AppContext.BaseDirectory, "session_tmp.json");
        }

        public string FilePath => _filePath;
        public string TempFilePath => _tempFilePath;

        public async Task<List<SessionParams>> LoadListAsync()
        {
            try
            {
                if (!File.Exists(_filePath)) return new List<SessionParams>();
                await using var fs = File.OpenRead(_filePath);
                var list = await JsonSerializer.DeserializeAsync<List<SessionParams>>(fs, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    WriteIndented = true
                });
                return list ?? new List<SessionParams>();
            }
            catch
            {
                return new List<SessionParams>();
            }
        }

        public async Task SaveListAsync(IEnumerable<SessionParams> data)
        {
            await SaveListToPathAsync(_filePath, data);
        }

        public async Task SaveTempListAsync(IEnumerable<SessionParams> data)
        {
            await SaveListToPathAsync(_tempFilePath, data);
        }

        private static async Task SaveListToPathAsync(string path, IEnumerable<SessionParams> data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var fs = File.Create(path);
            await JsonSerializer.SerializeAsync(fs, data.ToList(), new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
