using SQLite;

namespace GinkgoArticleParser.Models
{
    public class HistoryModel
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
        public string Timespan { get; set; }
    }
}
