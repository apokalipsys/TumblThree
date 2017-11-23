﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using TumblThree.Applications.DataModels;
using TumblThree.Applications.Downloader;
using TumblThree.Applications.Properties;
using TumblThree.Applications.Services;
using TumblThree.Domain;
using TumblThree.Domain.Models;

namespace TumblThree.Applications.Crawler
{
    [Export(typeof(ICrawler))]
    [ExportMetadata("BlogType", BlogTypes.tumblr)]
    public class TumblrBlogCrawler : AbstractCrawler, ICrawler
    {
        public TumblrBlogCrawler(IShellService shellService, CancellationToken ct, PauseToken pt,
            IProgress<DownloadProgress> progress, ICrawlerService crawlerService, ISharedCookieService cookieService, IDownloader downloader, BlockingCollection<TumblrPost> producerConsumerCollection, IBlog blog)
            : base(shellService, ct, pt, progress, crawlerService, cookieService, downloader, producerConsumerCollection, blog)
        {
        }

        public override async Task IsBlogOnlineAsync()
        {
            try
            {
                await GetApiPageAsync(1);
                blog.Online = true;
            }
            catch (WebException webException)
            {
                if (webException.Status == WebExceptionStatus.ProtocolError && webException.Response != null)
                {
                    var resp = (HttpWebResponse)webException.Response;
                    if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        Logger.Error("TumblrBlogCrawler:IsBlogOnlineAsync:WebException {0}", webException);
                        shellService.ShowError(webException, Resources.PasswordProtected, blog.Name);
                        blog.Online = true;
                        return;
                    }
                }
                if (webException.Status == WebExceptionStatus.ProtocolError && webException.Response != null)
                {
                    var resp = (HttpWebResponse)webException.Response;
                    if ((int)resp.StatusCode == 429)
                    {
                        Logger.Error("TumblrBlogCrawler:IsBlogOnlineAsync:WebException {0}", webException);
                        shellService.ShowError(webException, Resources.LimitExceeded, blog.Name);
                        blog.Online = true;
                        return;
                    }
                }
                blog.Online = false;
            }
        }

        public override async Task UpdateMetaInformationAsync()
        {
            try
            {
                await UpdateMetaInformation();
            }
            catch (WebException webException)
            {
                var webRespStatusCode = (int)((HttpWebResponse)webException?.Response).StatusCode;
                if (webRespStatusCode == 429)
                {
                    Logger.Error("TumblrBlogCrawler:UpdateMetaInformationAsync:WebException {0}", webException);
                    shellService.ShowError(webException, Resources.LimitExceeded, blog.Name);
                }
            }
        }

        private async Task UpdateMetaInformation()
        {
            if (blog.Online)
            {
                XDocument document = await GetApiPageAsync(1);

                if (document.Root != null)
                {
                    blog.Title = document.Element("tumblr").Element("tumblelog").Attribute("title")?.Value;
                    blog.Description = document.Element("tumblr").Element("tumblelog")?.Value;
                    blog.TotalCount = int.Parse(document.Element("tumblr").Element("posts").Attribute("total")?.Value);
                }
            }
        }

        public async Task Crawl()
        {
            Logger.Verbose("TumblrBlogCrawler.Crawl:Start");

            Task<Tuple<ulong, bool>> grabber = GetUrlsAsync();
            Task<bool> download = downloader.DownloadBlogAsync();
            Tuple<ulong, bool> grabberResult = await grabber;
            bool apiLimitHit = grabberResult.Item2;

            UpdateProgressQueueInformation(Resources.ProgressUniqueDownloads);

            blog.DuplicatePhotos = DetermineDuplicates(PostTypes.Photo);
            blog.DuplicateVideos = DetermineDuplicates(PostTypes.Video);
            blog.DuplicateAudios = DetermineDuplicates(PostTypes.Audio);
            blog.TotalCount = (blog.TotalCount - blog.DuplicatePhotos - blog.DuplicateAudios - blog.DuplicateVideos);

            CleanCollectedBlogStatistics();

            bool finishedDownloading = await download;

            if (!ct.IsCancellationRequested)
            {
                blog.LastCompleteCrawl = DateTime.Now;
                if (finishedDownloading && !apiLimitHit)
                {
                    blog.LastId = grabberResult.Item1;
                }
            }

            blog.Save();

            UpdateProgressQueueInformation("");
        }

        private new async Task<XDocument> RequestDataAsync(string url)
        {
            var requestRegistration = new CancellationTokenRegistration();
            try
            {
                HttpWebRequest request = CreateGetReqeust(url);
                request.Credentials = new NetworkCredential(blog.Name + "tumblr.com", blog.Password);
                requestRegistration = ct.Register(() => request.Abort());

                using (var response = await request.GetResponseAsync() as HttpWebResponse)
                {
                    using (var stream = GetStreamForApiRequest(response.GetResponseStream()))
                    {
                        using (var buffer = new BufferedStream(stream))
                        {
                            using (var reader = new StreamReader(buffer))
                            {
                                return XDocument.Load(reader);
                            }
                        }
                    }
                }
            }
            finally
            {
                requestRegistration.Dispose();
            }
        }

        private string GetApiUrl(string url, int count, int start = 0)
        {
            if (url.Last<char>() != '/')
            {
                url += "/api/read";
            }
            else
            {
                url += "api/read";
            }

            var parameters = new Dictionary<string, string>
            {
                { "num", count.ToString() }
            };
            if (start > 0)
            {
                parameters["start"] = start.ToString();
            }
            return url + "?" + UrlEncode(parameters);
        }

        private async Task<XDocument> GetApiPageAsync(int pageId)
        {
            string url = GetApiUrl(blog.Url, blog.PageSize, pageId * blog.PageSize);

            if (shellService.Settings.LimitConnections)
            {
                crawlerService.Timeconstraint.Acquire();
                return await RequestDataAsync(url).TimeoutAfter(shellService.Settings.TimeOut);
            }
            return await RequestDataAsync(url).TimeoutAfter(shellService.Settings.TimeOut);
        }

        private async Task UpdateTotalPostCountAsync()
        {
            try
            {
                await UpdateTotalPostCount();
            }
            catch (WebException webException)
            {
                var webRespStatusCode = (int)((HttpWebResponse)webException?.Response).StatusCode;
                if (webRespStatusCode == 429)
                {
                    Logger.Error("TumblrBlogCrawler:UpdateTotalPostCountAsync:WebException {0}", webException);
                    shellService.ShowError(webException, Resources.LimitExceeded, blog.Name);
                }
                blog.Posts = 0;
            }
        }

        private async Task UpdateTotalPostCount()
        {
            XDocument document = await GetApiPageAsync(1);

            int totalPosts;
            int.TryParse(document?.Element("tumblr").Element("posts").Attribute("total").Value, out totalPosts);
            blog.Posts = totalPosts;
        }

        private async Task<ulong> GetHighestPostIdAsync()
        {
            try
            {
                return await GetHighestPostId();
            }
            catch (WebException webException)
            {
                var webRespStatusCode = (int)((HttpWebResponse)webException?.Response).StatusCode;
                if (webRespStatusCode == 429)
                {
                    Logger.Error("TumblrBlogCrawler:GetHighestPostIdAsync:WebException {0}", webException);
                    shellService.ShowError(webException, Resources.LimitExceeded, blog.Name);
                }
                return 0;
            }
        }

        private async Task<ulong> GetHighestPostId()
        {
            XDocument document = await GetApiPageAsync(1);

            ulong highestId;
            ulong.TryParse(document?.Element("tumblr").Element("posts").Element("post")?.Attribute("id").Value, out highestId);
            return highestId;
        }

        private async Task<Tuple<ulong, bool>> GetUrlsAsync()
        {
            var semaphoreSlim = new SemaphoreSlim(shellService.Settings.ConcurrentScans);
            var trackedTasks = new List<Task>();
            var apiLimitHit = false;
            var completeGrab = true;

            await UpdateTotalPostCountAsync();
            int totalPosts = blog.Posts;

            ulong highestId = await GetHighestPostIdAsync();

            foreach (int pageNumber in GetPageNumbers())
            {
                await semaphoreSlim.WaitAsync();

                if (!completeGrab)
                {
                    break;
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }
                if (pt.IsPaused)
                {
                    pt.WaitWhilePausedWithResponseAsyc().Wait();
                }

                trackedTasks.Add(new Func<Task>(async () =>
                {
                    try
                    {
                        XDocument document = await GetApiPageAsync(pageNumber);

                        completeGrab = CheckPostAge(document);

                        if (!string.IsNullOrWhiteSpace(blog.Tags))
                        {
                            tags = blog.Tags.Split(',').Select(x => x.Trim()).ToList();
                        }

                        AddUrlsToDownloadList(document);
                    }
                    catch (WebException webException) when ((webException.Response != null))
                    {
                        var webRespStatusCode = (int)((HttpWebResponse)webException?.Response).StatusCode;
                        if (webRespStatusCode == 429)
                        {
                            apiLimitHit = true;
                            Logger.Error("TumblrBlogCrawler:GetUrls:WebException {0}", webException);
                            shellService.ShowError(webException, Resources.LimitExceeded, blog.Name);
                        }
                    }
                    catch
                    {                        
                    }
                    finally
                    {
                        semaphoreSlim.Release();
                    }

                    numberOfPagesCrawled += blog.PageSize;
                    UpdateProgressQueueInformation(Resources.ProgressGetUrlLong, numberOfPagesCrawled, totalPosts);
                })());
            }
            await Task.WhenAll(trackedTasks);

            producerConsumerCollection.CompleteAdding();

            //if (!ct.IsCancellationRequested && completeGrab)
            //{
                UpdateBlogStats();
            //}

            return new Tuple<ulong, bool>(highestId, apiLimitHit);
        }

        private bool PostWithinTimeSpan(XElement post)
        {
            if (!(string.IsNullOrEmpty(blog.DownloadFrom) && string.IsNullOrEmpty(blog.DownloadTo)))
            {
                long downloadFromUnixTime = 0;
                long downloadToUnixTime = long.MaxValue;
                if (!string.IsNullOrEmpty(blog.DownloadFrom))
                {
                    DateTime downloadFrom = DateTime.ParseExact(blog.DownloadFrom, "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None);
                    downloadFromUnixTime = new DateTimeOffset(downloadFrom).ToUnixTimeSeconds();
                }
                if (!string.IsNullOrEmpty(blog.DownloadTo))
                {
                    DateTime downloadTo = DateTime.ParseExact(blog.DownloadTo, "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None);
                    downloadToUnixTime = new DateTimeOffset(downloadTo).ToUnixTimeSeconds();
                }
                long postTime = 0;
                long.TryParse(post.Attribute("unix-timestamp").Value, out postTime);
                if (downloadFromUnixTime >= postTime || postTime >= downloadToUnixTime)
                    return false;
            }
            return true;
        }

        private bool CheckPostAge(XContainer document)
        {
            ulong highestPostId = 0;
            ulong.TryParse(document.Element("tumblr").Element("posts").Element("post")?.Attribute("id").Value,
                out highestPostId);

            if (highestPostId < GetLastPostId())
            {
                return false;
            }
            return true;
        }

        private void AddUrlsToDownloadList(XContainer document)
        {
            try
            {
                AddPhotoUrlToDownloadList(document);
                AddVideoUrlToDownloadList(document);
                AddAudioUrlToDownloadList(document);
                AddTextUrlToDownloadList(document);
                AddQuoteUrlToDownloadList(document);
                AddLinkUrlToDownloadList(document);
                AddConversationUrlToDownloadList(document);
                AddAnswerUrlToDownloadList(document);
                AddPhotoMetaUrlToDownloadList(document);
                AddVideoMetaUrlToDownloadList(document);
                AddAudioMetaUrlToDownloadList(document);
            }
            catch (NullReferenceException)
            {

            }
        }

        private bool CheckIfDownloadRebloggedPosts(XElement post)
        {
            if (!blog.DownloadRebloggedPosts)
            {
                if (!post.Attributes("reblogged-from-url").Any())
                    return true;
                return false;
            }
            return true;
        }

        private void AddPhotoUrlToDownloadList(XContainer document)
        {
            if (blog.DownloadPhoto)
            {
                foreach (XElement post in document.Descendants("post"))
                {
                    if (!PostWithinTimeSpan(post))
                        continue;
                    if (post.Attribute("type").Value == "photo" && (!tags.Any() ||
                        post.Descendants("tag").Any(x => tags.Contains(x.Value, StringComparer.OrdinalIgnoreCase))))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                        {
                            AddPhotoUrl(post);
                            AddPhotoSetUrl(post);
                            if (post.Element("photo-caption") != null)
                            {
                                var postCopy = new XElement(post);
                                postCopy.Elements("photo-url").Remove();
                                AddInlinePhotoUrl(postCopy);
                            }
                        }
                    }
                }

                // check for inline images
                foreach (XElement post in document.Descendants("post").Where(p => p.Attribute("type").Value != "photo"))
                {
                    if (!PostWithinTimeSpan(post))
                        continue;
                    if (!tags.Any() || post.Descendants("tag").Any(x => tags.Contains(x.Value, StringComparer.OrdinalIgnoreCase)))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                            AddInlinePhotoUrl(post);
                    }
                }
            }
        }

        private void AddVideoUrlToDownloadList(XContainer document)
        {
            if (blog.DownloadVideo)
            {
                foreach (XElement post in document.Descendants("post"))
                {
                    if (!PostWithinTimeSpan(post))
                        continue;
                    if (post.Attribute("type").Value == "video" && (!tags.Any() ||
                        post.Descendants("tag").Any(x => tags.Contains(x.Value, StringComparer.OrdinalIgnoreCase))))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                            AddVideoUrl(post);
                    }
                }

                // check for inline videos
                foreach (XElement post in document.Descendants("post").Where(p => p.Attribute("type").Value != "video"))
                {
                    if (!PostWithinTimeSpan(post))
                        continue;
                    if (!tags.Any() || post.Descendants("tag").Any(x => tags.Contains(x.Value, StringComparer.OrdinalIgnoreCase)))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                        {
                            if (post.Element("video-caption") != null)
                            {
                                var postCopy = new XElement(post);
                                postCopy.Elements("video-player").Remove();
                                AddInlineVideoUrl(postCopy);
                            }
                        }
                    }
                }
            }
        }

        private void AddAudioUrlToDownloadList(XContainer document)
        {
            if (blog.DownloadAudio)
            {
                foreach (XElement post in document.Descendants("post"))
                {
                    if (!PostWithinTimeSpan(post))
                        continue;
                    if (post.Attribute("type").Value == "audio" && (!tags.Any() ||
                        post.Descendants("tag").Any(x => tags.Contains(x.Value, StringComparer.OrdinalIgnoreCase))))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                            AddAudioUrl(post);
                    }
                }
            }
        }

        private void AddTextUrlToDownloadList(XContainer document)
        {
            if (blog.DownloadText)
            {
                foreach (XElement post in document.Descendants("post"))
                {
                    if (!PostWithinTimeSpan(post))
                        continue;
                    if (post.Attribute("type").Value == "regular" && (!tags.Any() ||
                        post.Descendants("tag").Any(x => tags.Contains(x.Value, StringComparer.OrdinalIgnoreCase))))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                        {
                            string textBody = ParseText(post);
                            AddToDownloadList(new TumblrPost(PostTypes.Text, textBody, post.Attribute("id").Value));
                        }
                    }
                }
            }
        }

        private void AddQuoteUrlToDownloadList(XContainer document)
        {
            if (blog.DownloadQuote)
            {
                foreach (XElement post in document.Descendants("post"))
                {
                    if (!PostWithinTimeSpan(post))
                        continue;
                    if (post.Attribute("type").Value == "quote" && (!tags.Any() ||
                        post.Descendants("tag").Any(x => tags.Contains(x.Value, StringComparer.OrdinalIgnoreCase))))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                        {
                            string textBody = ParseQuote(post);
                            AddToDownloadList(new TumblrPost(PostTypes.Quote, textBody, post.Attribute("id").Value));
                        }
                    }
                }
            }
        }

        private void AddLinkUrlToDownloadList(XContainer document)
        {
            if (blog.DownloadLink)
            {
                foreach (XElement post in document.Descendants("post"))
                {
                    if (!PostWithinTimeSpan(post))
                        continue;
                    if (post.Attribute("type").Value == "link" && (!tags.Any() ||
                        post.Descendants("tag").Any(x => tags.Contains(x.Value, StringComparer.OrdinalIgnoreCase))))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                        {
                            string textBody = ParseLink(post);
                            AddToDownloadList(new TumblrPost(PostTypes.Link, textBody, post.Attribute("id").Value));
                        }
                    }
                }
            }
        }

        private void AddConversationUrlToDownloadList(XContainer document)
        {
            if (blog.DownloadConversation)
            {
                foreach (XElement post in document.Descendants("post"))
                {
                    if (!PostWithinTimeSpan(post))
                        continue;
                    if (post.Attribute("type").Value == "conversation" && (!tags.Any() ||
                        post.Descendants("tag").Any(x => tags.Contains(x.Value, StringComparer.OrdinalIgnoreCase))))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                        {
                            string textBody = ParseConversation(post);
                            AddToDownloadList(new TumblrPost(PostTypes.Conversation, textBody, post.Attribute("id").Value));
                        }
                    }
                }
            }
        }

        private void AddAnswerUrlToDownloadList(XContainer document)
        {
            if (blog.DownloadAnswer)
            {
                foreach (XElement post in document.Descendants("post"))
                {
                    if (!PostWithinTimeSpan(post))
                        continue;
                    if (post.Attribute("type").Value == "answer" && (!tags.Any() ||
                        post.Descendants("tag").Any(x => tags.Contains(x.Value, StringComparer.OrdinalIgnoreCase))))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                        {
                            string textBody = ParseAnswer(post);
                            AddToDownloadList(new TumblrPost(PostTypes.Answer, textBody, post.Attribute("id").Value));
                        }
                    }
                }
            }
        }

        private void AddPhotoMetaUrlToDownloadList(XContainer document)
        {
            if (blog.CreatePhotoMeta)
            {
                foreach (XElement post in document.Descendants("post"))
                {
                    if (!PostWithinTimeSpan(post))
                        continue;
                    if (post.Attribute("type").Value == "photo" && (!tags.Any() ||
                        post.Descendants("tag").Any(x => tags.Contains(x.Value, StringComparer.OrdinalIgnoreCase))))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                        {
                            string textBody = ParsePhotoMeta(post);
                            AddToDownloadList(new TumblrPost(PostTypes.PhotoMeta, textBody, post.Attribute("id").Value));
                        }
                    }
                }
            }
        }

        private void AddVideoMetaUrlToDownloadList(XContainer document)
        {
            if (blog.CreateVideoMeta)
            {
                foreach (XElement post in document.Descendants("post"))
                {
                    if (!PostWithinTimeSpan(post))
                        continue;
                    if (post.Attribute("type").Value == "video" && (!tags.Any() ||
                        post.Descendants("tag").Any(x => tags.Contains(x.Value, StringComparer.OrdinalIgnoreCase))))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                        {
                            string textBody = ParseVideoMeta(post);
                            AddToDownloadList(new TumblrPost(PostTypes.VideoMeta, textBody, post.Attribute("id").Value));
                        }
                    }
                }
            }
        }

        private void AddAudioMetaUrlToDownloadList(XContainer document)
        {
            if (blog.CreateAudioMeta)
            {
                foreach (XElement post in document.Descendants("post"))
                {
                    if (!PostWithinTimeSpan(post))
                        continue;
                    if (post.Attribute("type").Value == "audio" && (!tags.Any() ||
                        post.Descendants("tag").Any(x => tags.Contains(x.Value, StringComparer.OrdinalIgnoreCase))))
                    {
                        if (CheckIfDownloadRebloggedPosts(post))
                        {
                            string textBody = ParseAudioMeta(post);
                            AddToDownloadList(new TumblrPost(PostTypes.AudioMeta, textBody, post.Attribute("id").Value));
                        }
                    }
                }
            }
        }

        private string ParseImageUrl(XContainer post)
        {
            string imageUrl = post.Elements("photo-url")
                      .FirstOrDefault(photo_url => photo_url.Attribute("max-width")
                      .Value == ImageSize()).Value;

            return imageUrl;
        }

        private void AddInlinePhotoUrl(XElement post)
        {
            var regex = new Regex("\"(http[A-Za-z0-9_/:.]*media.tumblr.com[A-Za-z0-9_/:.]*(jpg|png|gif))\"");
            foreach (Match match in regex.Matches(post.Value))
            {
                string imageUrl = match.Groups[1].Value;
                if (imageUrl.Contains("avatar") || imageUrl.Contains("previews"))
                    continue;
                if (blog.SkipGif && imageUrl.EndsWith(".gif"))
                {
                    continue;
                }
                imageUrl = ResizeTumblrImageUrl(imageUrl);
                AddToDownloadList(new TumblrPost(PostTypes.Photo, imageUrl, post.Attribute("id").Value, post.Attribute("unix-timestamp").Value));
            }
        }

        private void AddInlineVideoUrl(XElement post)
        {
            var regex = new Regex("\"(http[A-Za-z0-9_/:.]*.com/video_file/[A-Za-z0-9_/:.]*)\"");
            foreach (Match match in regex.Matches(post.Value))
            {
                string videoUrl = match.Groups[1].Value;
                if (shellService.Settings.VideoSize == 1080)
                {
                    AddToDownloadList(new TumblrPost(PostTypes.Video, videoUrl.Replace("/480", "") + ".mp4", post.Attribute("id").Value, post.Attribute("unix-timestamp").Value));
                }
                else if (shellService.Settings.VideoSize == 480)
                {
                    AddToDownloadList(new TumblrPost(PostTypes.Video,
                        "https://vt.tumblr.com/" + videoUrl.Replace("/480", "").Split('/').Last() + "_480.mp4",
                        post.Attribute("id").Value, post.Attribute("unix-timestamp").Value));
                }
            }
        }

        private void AddPhotoUrl(XElement post)
        {
            string imageUrl = ParseImageUrl(post);
            if (blog.SkipGif && imageUrl.EndsWith(".gif"))
            {
                return;
            }
            AddToDownloadList(new TumblrPost(PostTypes.Photo, imageUrl, post.Attribute("id").Value, post.Attribute("unix-timestamp").Value));
        }

        private void AddPhotoSetUrl(XElement post)
        {
            if (!post.Descendants("photoset").Any())
            {
                return;
            }
            foreach (string imageUrl in post.Descendants("photoset")
                                            .Descendants("photo")
                                            .Select(ParseImageUrl)
                                            .Where(imageUrl => !blog.SkipGif || !imageUrl.EndsWith(".gif")))
            {
                AddToDownloadList(new TumblrPost(PostTypes.Photo, imageUrl, post.Attribute("id").Value, post.Attribute("unix-timestamp").Value));
            }
        }

        private void AddVideoUrl(XElement post)
        {
            string videoUrl = post.Descendants("video-player")
                                  .Select(result => Regex.Match(result.Value, "<source src=\"([\\S]*)\"")
                                                         .Groups[1].Value)
                                  .FirstOrDefault();

            if (shellService.Settings.VideoSize == 1080)
            {

                AddToDownloadList(new TumblrPost(PostTypes.Video,
                    "https://vt.tumblr.com/" + videoUrl.Replace("/480", "").Split('/').Last() + ".mp4",
                    post.Attribute("id").Value, post.Attribute("unix-timestamp").Value));
            }
            else if (shellService.Settings.VideoSize == 480)
            {

                AddToDownloadList(new TumblrPost(PostTypes.Video,
                    "https://vt.tumblr.com/" + videoUrl.Replace("/480", "").Split('/').Last() + "_480.mp4",
                    post.Attribute("id").Value, post.Attribute("unix-timestamp").Value));
            }
        }

        private void AddAudioUrl(XElement post)
        {
            string audioUrl = post.Descendants("audio-embed")
                .Select(result => Regex.Match(result.Value, "audio_file=([\\S]*)\"")
                .Groups[1].Value)
                .FirstOrDefault();
            audioUrl = System.Web.HttpUtility.UrlDecode(audioUrl);
            if (!audioUrl.EndsWith(".mp3"))
                audioUrl = audioUrl + ".mp3";

            AddToDownloadList(new TumblrPost(PostTypes.Audio, WebUtility.UrlDecode(audioUrl), post.Attribute("id").Value, post.Attribute("unix-timestamp").Value));
        }

        private static string ParsePhotoMeta(XElement post)
        {
            return string.Format(CultureInfo.CurrentCulture, Resources.PostId, post.Attribute("id").Value) + ", " +
                   string.Format(CultureInfo.CurrentCulture, Resources.Date, post.Attribute("date-gmt").Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.UrlWithSlug, post.Attribute("url-with-slug")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogKey, post.Attribute("reblog-key")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogUrl, post.Attribute("reblogged-from-url")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogName, post.Attribute("reblogged-from-name")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.PhotoUrl, post.Element("photo-url").Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.PhotoCaption, post.Element("photo-caption")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Tags,
                       string.Join(", ", post.Elements("tag")?.Select(x => x.Value).ToArray())) +
                   Environment.NewLine;
        }

        private static string ParseVideoMeta(XElement post)
        {
            return string.Format(CultureInfo.CurrentCulture, Resources.PostId, post.Attribute("id").Value) + ", " +
                   string.Format(CultureInfo.CurrentCulture, Resources.Date, post.Attribute("date-gmt").Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.UrlWithSlug, post.Attribute("url-with-slug")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogKey, post.Attribute("reblog-key")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogUrl, post.Attribute("reblogged-from-url")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogName, post.Attribute("reblogged-from-name")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.VideoPlayer, post.Element("video-player")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Tags,
                       string.Join(", ", post.Elements("tag")?.Select(x => x.Value).ToArray())) +
                   Environment.NewLine;
        }

        private static string ParseAudioMeta(XElement post)
        {
            return string.Format(CultureInfo.CurrentCulture, Resources.PostId, post.Attribute("id").Value) + ", " +
                   string.Format(CultureInfo.CurrentCulture, Resources.Date, post.Attribute("date-gmt").Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.UrlWithSlug, post.Attribute("url-with-slug")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogKey, post.Attribute("reblog-key")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogUrl, post.Attribute("reblogged-from-url")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogName, post.Attribute("reblogged-from-name")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.AudioCaption, post.Element("audio-caption")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Id3Artist, post.Element("id3-artist")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Id3Title, post.Element("id3-title")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Id3Track, post.Element("id3-track")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Id3Album, post.Element("id3-album")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Id3Year, post.Element("id3-year")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Tags,
                       string.Join(", ", post.Elements("tag")?.Select(x => x.Value).ToArray())) +
                   Environment.NewLine;
        }

        private static string ParseConversation(XElement post)
        {
            return string.Format(CultureInfo.CurrentCulture, Resources.PostId, post.Attribute("id").Value) + ", " +
                   string.Format(CultureInfo.CurrentCulture, Resources.Date, post.Attribute("date-gmt").Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.UrlWithSlug, post.Attribute("url-with-slug")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogKey, post.Attribute("reblog-key")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogUrl, post.Attribute("reblogged-from-url")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogName, post.Attribute("reblogged-from-name")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Conversation, post.Element("conversation-text")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Tags,
                       string.Join(", ", post.Elements("tag")?.Select(x => x.Value).ToArray())) +
                   Environment.NewLine;
        }

        private static string ParseLink(XElement post)
        {
            return string.Format(CultureInfo.CurrentCulture, Resources.PostId, post.Attribute("id").Value) + ", " +
                   string.Format(CultureInfo.CurrentCulture, Resources.Date, post.Attribute("date-gmt").Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.UrlWithSlug, post.Attribute("url-with-slug")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogKey, post.Attribute("reblog-key")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogUrl, post.Attribute("reblogged-from-url")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogName, post.Attribute("reblogged-from-name")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Link, post.Element("link-text")?.Value) +
                   Environment.NewLine + post.Element("link-url")?.Value +
                   Environment.NewLine + post.Element("link-description")?.Value +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Tags,
                       string.Join(", ", post.Elements("tag")?.Select(x => x.Value).ToArray())) +
                   Environment.NewLine;
        }

        private static string ParseQuote(XElement post)
        {
            return string.Format(CultureInfo.CurrentCulture, Resources.PostId, post.Attribute("id").Value) + ", " +
                   string.Format(CultureInfo.CurrentCulture, Resources.Date, post.Attribute("date-gmt").Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.UrlWithSlug, post.Attribute("url-with-slug")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogKey, post.Attribute("reblog-key")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogUrl, post.Attribute("reblogged-from-url")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogName, post.Attribute("reblogged-from-name")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Quote, post.Element("quote-text")?.Value) +
                   Environment.NewLine + post.Element("quote-source")?.Value +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Tags,
                       string.Join(", ", post.Elements("tag")?.Select(x => x.Value).ToArray())) +
                   Environment.NewLine;
        }

        private static string ParseText(XElement post)
        {
            return string.Format(CultureInfo.CurrentCulture, Resources.PostId, post.Attribute("id").Value) + ", " +
                   string.Format(CultureInfo.CurrentCulture, Resources.Date, post.Attribute("date-gmt").Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.UrlWithSlug, post.Attribute("url-with-slug")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogKey, post.Attribute("reblog-key")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogUrl, post.Attribute("reblogged-from-url")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogName, post.Attribute("reblogged-from-name")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Title, post.Element("regular-title")?.Value) +
                   Environment.NewLine + post.Element("regular-body")?.Value +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Tags,
                       string.Join(", ", post.Elements("tag")?.Select(x => x.Value).ToArray())) +
                   Environment.NewLine;
        }

        private static string ParseAnswer(XElement post)
        {
            return string.Format(CultureInfo.CurrentCulture, Resources.PostId, post.Attribute("id").Value) + ", " +
                   string.Format(CultureInfo.CurrentCulture, Resources.Date, post.Attribute("date-gmt").Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.UrlWithSlug, post.Attribute("url-with-slug")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogKey, post.Attribute("reblog-key")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogUrl, post.Attribute("reblogged-from-url")?.Value) +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.ReblogName, post.Attribute("reblogged-from-name")?.Value) +
                   Environment.NewLine +
                   post.Element("question")?.Value +
                   Environment.NewLine +
                   post.Element("answer")?.Value +
                   Environment.NewLine +
                   string.Format(CultureInfo.CurrentCulture, Resources.Tags,
                       string.Join(", ", post.Elements("tag")?.Select(x => x.Value).ToArray())) +
                   Environment.NewLine;
        }
    }
}
