using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace GinkgoArticleParser.Helpers
{
    public class FileHelper
    {
        // 获取 SQLite 数据库文件的完整路径
        private static string GetDatabasePath()
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GinkgoArticleParser");

            // 确保文件夹存在
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            return Path.Combine(folder, "History.db3");
        }

        /// <summary>
        /// 将数据库文件导出到用户的“下载”文件夹或通过共享/保存对话框导出。
        /// </summary>
        /// <returns>操作是否成功。</returns>
        public async static Task<bool> ExportDatabaseAsync()
        {
            string sourceDbPath = GetDatabasePath();
            if (!File.Exists(sourceDbPath))
            {
                // Debug.WriteLine("数据库文件不存在。");
                return false;
            }

            // 导出文件的新名称（添加时间戳避免覆盖）
            string destinationFileName = $"History-{DateTime.Now:yyyyMMdd_HHmmss}.db3";

            try
            {
#if WINDOWS || MACCATALYST
                // --- 适用于 Windows 和 Mac Catalyst ---
                // 在桌面平台上，可以直接访问用户目录下的 Downloads 文件夹。
                string downloadsPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string destinationFolder = Path.Combine(downloadsPath, "Downloads");

                if (!Directory.Exists(destinationFolder))
                {
                    Directory.CreateDirectory(destinationFolder);
                }

                string destinationPath = Path.Combine(destinationFolder, destinationFileName);

                // 执行文件复制
                File.Copy(sourceDbPath, destinationPath, true);

                // 可以添加一个提示
                await Shell.Current.DisplayAlert("导出成功", $"数据库已保存到: {destinationPath}", "确定");
                return true;

#elif ANDROID || IOS
                // --- 适用于 Android 和 iOS (移动平台) ---
                // 移动平台出于安全考虑，不能直接访问公共目录。
                // 最佳实践是使用 MAUI 的 Sharing API 弹出一个对话框，让用户选择保存位置。

                // 1. 将文件复制到应用的缓存目录
                string tempPath = Path.Combine(FileSystem.Current.CacheDirectory, destinationFileName);
                Debug.WriteLine(tempPath);
                File.Copy(sourceDbPath, tempPath, true);

                // 2. 弹出共享/保存对话框
                await Share.RequestAsync(new ShareFileRequest
                {
                    Title = "导出历史记录数据库",
                    File = new ShareFile(tempPath)
                });

                // 3. (可选) 清理临时文件
                //File.Delete(tempPath);

                return true;

#else
            // 其他平台（如 Linux 或未指定）
            // Debug.WriteLine("当前平台不支持直接导出到 Downloads 文件夹。");
            return false;
#endif
            }
            catch (Exception ex)
            {
                // Debug.WriteLine($"导出数据库时发生错误: {ex.Message}");
                // await Shell.Current.DisplayAlert("导出失败", $"发生错误: {ex.Message}", "确定");
                return false;
            }
        }

        /// <summary>
        /// 将数据库文件导入到应用沙箱中，覆盖现有数据库。
        /// 警告：导入后可能需要重启应用才能完全生效，因为可能存在旧的数据库连接。
        /// </summary>
        /// <returns>操作是否成功。</returns>
        public async static Task<bool> ImportDatabaseAsync(Func<Task> closeDbConnectionAsync)
        {
            try
            {
                // 1. 使用文件选择器让用户选择文件
                var fileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                // 适用于 iOS (统一类型标识符)
                { DevicePlatform.iOS, new[] { "public.database" } },
                // 适用于 Android (MIME 类型)
                { DevicePlatform.Android, new[] { "application/octet-stream","application/vnd.sqlite3", "application/x-sqlite3" } },
                // 适用于 Windows/Mac (文件扩展名)
                { DevicePlatform.WinUI, new[] { ".db3" } },
                { DevicePlatform.macOS, new[] { ".db3" } }
            });

                var result = await FilePicker.PickAsync(new PickOptions
                {
                    FileTypes = fileTypes,
                    PickerTitle = "选择 SQLite 数据库文件 (.db3)"
                });

                // 用户取消选择
                if (result == null)
                {
                    Debug.WriteLine("用户取消了文件选择。");
                    return false;
                }

                string sourcePath = result.FullPath;
                string targetDbPath = GetDatabasePath();

                // 简单的文件名验证，确保是数据库文件
                if (!sourcePath.EndsWith(".db3", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine($"文件类型或名称不匹配: {sourcePath}");
                    // 在实际应用中，这里应该向用户显示一个警告
                    return false;
                }

                //2.移除目标文件的只读属性（如果有）
                RemoveReadOnly(sourcePath);
                RemoveReadOnly(targetDbPath);

                // 3. 复制文件，覆盖应用沙箱中的现有数据库
                // **重要提醒：这会覆盖旧数据库。在生产环境中，需要确保在复制前关闭数据库连接。**
                File.Copy(sourcePath, targetDbPath, true);

#if WINDOWS || MACCATALYST
                // 可以添加一个提示
                await Shell.Current.DisplayAlert(" 导入成功", $"数据库已从 {sourcePath} 导入并覆盖到 {targetDbPath}", "确定");
#endif
                Debug.WriteLine($"数据库已从 {sourcePath} 导入并覆盖到 {targetDbPath}");

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"导入数据库时发生错误: {ex.Message}");
                // 建议在 UI 上显示错误信息
                return false;
            }
        }

        public static void RemoveReadOnly(string filePath)
        {
            // 1. 检查文件是否存在
            if (!File.Exists(filePath))
            {
                Debug.WriteLine($"错误：文件未找到 -> {filePath}");
                return;
            }

            // 2. 获取当前文件的属性
            FileAttributes attributes = File.GetAttributes(filePath);

            // 3. 检查文件是否确实是只读状态
            if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
            {
                try
                {
                    // 4. 使用按位非运算符 (~) 和按位与运算符 (&) 来清除 ReadOnly 标志
                    File.SetAttributes(filePath, attributes & ~FileAttributes.ReadOnly);
                    Debug.WriteLine($"成功：已取消文件 {filePath} 的只读属性。");
                }
                catch (Exception ex)
                {
                    // 捕获权限或其他可能发生的错误
                    Debug.WriteLine($"操作失败：无法设置文件属性。错误信息：{ex.Message}");
                }
            }
            else
            {
                Debug.WriteLine($"信息：文件 {filePath} 本身就没有只读属性。");
            }
        }
    }
}
