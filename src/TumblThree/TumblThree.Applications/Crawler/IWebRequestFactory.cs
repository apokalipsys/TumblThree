using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace TumblThree.Applications.Crawler
{
    public interface IWebRequestFactory
    {
        HttpWebRequest CreateGetReqeust(string url);

        HttpWebRequest CreateGetXhrReqeust(string url, string referer = "");

        HttpWebRequest CreatePostReqeust(string url, string referer = "", Dictionary<string, string> headers = null);

        HttpWebRequest CreatePostXhrReqeust(string url, string referer = "", Dictionary<string, string> headers = null);

        Task<string> ReadReqestToEnd(HttpWebRequest request);

        Stream GetStreamForApiRequest(Stream stream);

    }
}