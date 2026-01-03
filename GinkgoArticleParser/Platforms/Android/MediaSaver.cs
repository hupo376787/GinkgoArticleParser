#if ANDROID
using System;
using System.IO;
using System.Threading.Tasks;

namespace GinkgoArticleParser.Platforms.Android
{
    public static class MediaSaver
    {
        /// <summary>
        /// 将图片保存到公共 Pictures/GinkgoArticleParser 目录（使用 MediaStore 在 Android Q+ 上插入）
        /// 返回已保存的 Uri（Android Q+）或文件路径（旧版）。
        /// </summary>
        public static async Task<string?> SaveImageAsync(byte[] imageData, string fileName, string subFolder, string mimeType = "image/jpeg")
        {
            try
            {
                // 使用完全限定名以避免与 MAUI 的 Application 或当前命名空间冲突
                var context = global::Android.App.Application.Context;

                // Android 10(Q)+ 使用 MediaStore 插入公共媒体
                if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.Q)
                {
                    var values = new global::Android.Content.ContentValues();
                    values.Put(global::Android.Provider.MediaStore.IMediaColumns.DisplayName, fileName);
                    values.Put(global::Android.Provider.MediaStore.IMediaColumns.MimeType, mimeType);
                    // 相对路径示例: Pictures/GinkgoArticleParser
                    string destFolder = Path.Combine(global::Android.OS.Environment.DirectoryPictures, subFolder);
                    values.Put(global::Android.Provider.MediaStore.Images.ImageColumns.RelativePath, destFolder);
                    values.Put(global::Android.Provider.MediaStore.Images.ImageColumns.IsPending, 1);

                    var uri = context.ContentResolver.Insert(global::Android.Provider.MediaStore.Images.Media.ExternalContentUri, values);
                    if (uri == null) return null;

                    using (var stream = context.ContentResolver.OpenOutputStream(uri))
                    {
                        if (stream == null) return null;
                        await stream.WriteAsync(imageData, 0, imageData.Length).ConfigureAwait(false);
                        await stream.FlushAsync().ConfigureAwait(false);
                    }

                    values.Clear();
                    values.Put(global::Android.Provider.MediaStore.Images.ImageColumns.IsPending, 0);
                    context.ContentResolver.Update(uri, values, null, null);

                    return uri.ToString();
                }
                else
                {
                    // Android 9 及以下：写入公共 Pictures 文件夹并触发 MediaScanner
                    var pictures = global::Android.OS.Environment.GetExternalStoragePublicDirectory(global::Android.OS.Environment.DirectoryPictures);
                    var targetFolder = Path.Combine(pictures.AbsolutePath, "GinkgoArticleParser");
                    Directory.CreateDirectory(targetFolder);

                    var filePath = Path.Combine(targetFolder, fileName);
                    await File.WriteAllBytesAsync(filePath, imageData).ConfigureAwait(false);

                    // 通知系统扫描该文件，使其出现在相册/其它应用中
                    global::Android.Media.MediaScannerConnection.ScanFile(context, new[] { filePath }, new[] { mimeType }, null);

                    return filePath;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediaSaver error: {ex}");
                return null;
            }
        }

        /// <summary>
        /// 将视频保存到公共 Movies/GinkgoArticleParser 目录（Android Q+ 使用 MediaStore.Videos）。
        /// 返回已保存的 Uri（Android Q+）或文件路径（旧版）。
        /// </summary>
        public static async Task<string?> SaveVideoAsync(byte[] videoData, string fileName, string subFolder, string mimeType = "video/mp4")
        {
            try
            {
                var context = global::Android.App.Application.Context;

                if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.Q)
                {
                    var values = new global::Android.Content.ContentValues();
                    values.Put(global::Android.Provider.MediaStore.IMediaColumns.DisplayName, fileName);
                    values.Put(global::Android.Provider.MediaStore.IMediaColumns.MimeType, mimeType);
                    string destFolder = Path.Combine(global::Android.OS.Environment.DirectoryMovies, subFolder);
                    values.Put(global::Android.Provider.MediaStore.Video.VideoColumns.RelativePath, destFolder);
                    values.Put(global::Android.Provider.MediaStore.Video.VideoColumns.IsPending, 1);

                    var uri = context.ContentResolver.Insert(global::Android.Provider.MediaStore.Video.Media.ExternalContentUri, values);
                    if (uri == null) return null;

                    using (var stream = context.ContentResolver.OpenOutputStream(uri))
                    {
                        if (stream == null) return null;
                        await stream.WriteAsync(videoData, 0, videoData.Length).ConfigureAwait(false);
                        await stream.FlushAsync().ConfigureAwait(false);
                    }

                    values.Clear();
                    values.Put(global::Android.Provider.MediaStore.Video.VideoColumns.IsPending, 0);
                    context.ContentResolver.Update(uri, values, null, null);

                    return uri.ToString();
                }
                else
                {
                    var movies = global::Android.OS.Environment.GetExternalStoragePublicDirectory(global::Android.OS.Environment.DirectoryMovies);
                    var targetFolder = Path.Combine(movies.AbsolutePath, "GinkgoArticleParser");
                    Directory.CreateDirectory(targetFolder);

                    var filePath = Path.Combine(targetFolder, fileName);
                    await File.WriteAllBytesAsync(filePath, videoData).ConfigureAwait(false);

                    global::Android.Media.MediaScannerConnection.ScanFile(context, new[] { filePath }, new[] { mimeType }, null);
                    return filePath;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediaSaver.SaveVideoAsync error: {ex}");
                return null;
            }
        }
    }
}
#endif