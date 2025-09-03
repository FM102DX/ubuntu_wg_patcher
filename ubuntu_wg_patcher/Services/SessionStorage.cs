using System;
using System.IO;
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

        public async Task<SessionParams?> LoadAsync()
        {
            try
            {
                if (!File.Exists(_filePath)) return null;
                await using var fs = File.OpenRead(_filePath);
                return await JsonSerializer.DeserializeAsync<SessionParams>(fs, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    WriteIndented = true
                });
            }
            catch
            {
                return null;
            }
        }

        public async Task SaveAsync(SessionParams data)
        {
            await SaveToPathAsync(_filePath, data);
        }

        public async Task SaveTempAsync(SessionParams data)
        {
            await SaveToPathAsync(_tempFilePath, data);
        }

        private static async Task SaveToPathAsync(string path, SessionParams data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var fs = File.Create(path);
            await JsonSerializer.SerializeAsync(fs, data, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
