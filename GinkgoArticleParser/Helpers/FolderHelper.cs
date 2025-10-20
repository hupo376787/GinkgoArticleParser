using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GinkgoArticleParser.Helpers
{
    public class FolderHelper
    {
        public static string GetDownloadFolder()
        {

            string downloadPath = string.Empty;

#if WINDOWS
    // Windows 下载目录
    downloadPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
#elif ANDROID
            // Android Download 目录
            downloadPath = Path.Combine(Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryPictures).AbsolutePath);
#elif IOS
    // iOS 使用应用程序的文档目录
    downloadPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
#else
    // 其他平台可自定义
    downloadPath = FileSystem.AppDataDirectory;
#endif
            return downloadPath;
        }
    }
}