using GinkgoArticleParser.Enums;
using SQLite;

namespace GinkgoArticleParser.Models
{
    public class HistoryModel
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public PlatformsEnum Platform { get; set; }
        public string Title { get; set; }
        public string Author { get; set; }
        public string Url { get; set; }
        public string Timespan { get; set; }
        public DateTime PublishDateTime { get; set; }
        public DateTime DowloadDateTime { get; set; }
    }
}
