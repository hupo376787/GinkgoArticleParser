using System.Linq.Expressions;

namespace GinkgoArticleParser.Services
{
    public interface ISqliteService
    {
        Task InitAsync();

        Task CloseConnectionAsync();

        Task<int> InsertAsync<T>(T entity) where T : class, new();

        Task<int> UpdateAsync<T>(T entity) where T : class, new();

        Task<int> DeleteAsync<T>(T entity) where T : class, new();

        Task<List<T>> GetAllAsync<T>() where T : class, new();

        Task<T> GetByIdAsync<T>(object primaryKey) where T : class, new();

        Task<T> GetByUrlAsync<T>(string url) where T : class, new();

        Task<List<T>> GetPageAsync<T>(int pageIndex, int pageSize,
            Expression<Func<T, object>> orderBy = null,
            bool descending = false) where T : class, new();


    }
}
