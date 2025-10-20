using GinkgoArticleParser.Models;
using SQLite;
using System.Linq.Expressions;
using System.Reflection;

namespace GinkgoArticleParser.Services
{
    public class SqliteService : ISqliteService
    {
        private SQLiteAsyncConnection _db;

        public SqliteService()
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GinkgoArticleParser");

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            string dbPath = Path.Combine(folder, "History.db3");

            _db = new SQLiteAsyncConnection(dbPath);
        }

        public async Task InitAsync()
        {
            // 注册需要自动建表的实体
            await _db.CreateTableAsync<HistoryModel>();
            // 如果有更多实体可以继续加：
            // await _db.CreateTableAsync<Order>();
        }

        public async Task CloseConnectionAsync()
        {
            if (_db != null)
            {
                // **关键操作：关闭连接，释放文件锁**
                await _db.CloseAsync();
                //_db = null; // 置空，以便 InitAsync 能够重新建立连接
                System.Diagnostics.Debug.WriteLine("SQLite 连接已关闭。");
            }
        }

        public async Task<int> InsertAsync<T>(T entity) where T : class, new()
        {
            return await _db.InsertAsync(entity);
        }

        public async Task<int> UpdateAsync<T>(T entity) where T : class, new()
        {
            return await _db.UpdateAsync(entity);
        }

        public async Task<int> DeleteAsync<T>(T entity) where T : class, new()
        {
            return await _db.DeleteAsync(entity);
        }

        public async Task<List<T>> GetAllAsync<T>() where T : class, new()
        {
            return await _db.Table<T>().ToListAsync();
        }

        public async Task<T> GetByIdAsync<T>(object primaryKey) where T : class, new()
        {
            return await _db.FindAsync<T>(primaryKey);
        }

        // 泛型版 GetByUrlAsync
        public async Task<T> GetByUrlAsync<T>(string url) where T : class, new()
        {
            // 获取 T 的属性 Url
            var prop = typeof(T).GetProperty("Url", BindingFlags.Public | BindingFlags.Instance);
            if (prop == null)
                throw new Exception($"类型 {typeof(T).Name} 没有 Url 属性");

            var list = await _db.Table<T>().ToListAsync();

            // 找到第一个 Url 匹配的记录
            return list.FirstOrDefault(x =>
            {
                var value = prop.GetValue(x) as string;
                return value == url;
            });
        }

        // 确保 GetPageAsync 能正常工作 (简化为按 Id 降序)
        public async Task<List<T>> GetPageAsync<T>(
                int pageIndex,
                int pageSize,
                Expression<Func<T, object>> orderBy,
                bool descending = false) where T : class, new()
        {
            if (pageIndex < 1) pageIndex = 1;
            int skip = (pageIndex - 1) * pageSize;

            var query = _db.Table<T>();

            // 关键修复：直接在数据库查询中应用正确的排序
            AsyncTableQuery<T> orderedQuery;

            if (descending)
            {
                // 降序排序（最新数据在前）
                orderedQuery = query.OrderByDescending(orderBy);
            }
            else
            {
                // 升序排序
                orderedQuery = query.OrderBy(orderBy);
            }

            var list = await orderedQuery
                .Skip(skip) // 跳过前面的页
                .Take(pageSize) // 取当前页的数据
                .ToListAsync();

            // 注意：这里不再需要 list.Reverse()，因为排序已经在数据库层面完成。

            return list;
        }



    }
}
