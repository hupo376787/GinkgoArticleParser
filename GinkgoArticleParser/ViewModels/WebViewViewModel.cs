using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GinkgoArticleParser.ViewModels
{
    public partial class WebViewViewModel : BaseViewModel
    {
        public WebViewViewModel(string url)
        {
            WebUrl = url;
        }

        [ObservableProperty]
        private string webUrl;
    }
}
