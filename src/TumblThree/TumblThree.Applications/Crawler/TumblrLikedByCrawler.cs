﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using TumblThree.Applications.DataModels;
using TumblThree.Applications.Downloader;
using TumblThree.Applications.Properties;
using TumblThree.Applications.Services;
using TumblThree.Domain;
using TumblThree.Domain.Models;

namespace TumblThree.Applications.Crawler
{
    [Export(typeof(ICrawler))]
    [ExportMetadata("BlogType", BlogTypes.tlb)]
    public class TumblrLikedByCrawler : AbstractCrawler, ICrawler
    {
        public TumblrLikedByCrawler(IShellService shellService, CancellationToken ct, PauseToken pt,
            IProgress<DownloadProgress> progress, ICrawlerService crawlerService, IWebRequestFactory webRequestFactory, ISharedCookieService cookieService, IDownloader downloader, BlockingCollection<TumblrPost> producerConsumerCollection, IBlog blog)
            : base(shellService, ct, pt, progress, crawlerService, webRequestFactory, cookieService, downloader, producerConsumerCollection, blog)
        {
        }

        public async Task Crawl()
        {
            Logger.Verbose("TumblrLikedByCrawler.Crawl:Start");

            Task grabber = GetUrlsAsync();
            Task<bool> download = downloader.DownloadBlogAsync();

            await grabber;

            UpdateProgressQueueInformation(Resources.ProgressUniqueDownloads);
            blog.DuplicatePhotos = DetermineDuplicates(PostTypes.Photo);
            blog.DuplicateVideos = DetermineDuplicates(PostTypes.Video);
            blog.DuplicateAudios = DetermineDuplicates(PostTypes.Audio);
            blog.TotalCount = (blog.TotalCount - blog.DuplicatePhotos - blog.DuplicateAudios - blog.DuplicateVideos);

            CleanCollectedBlogStatistics();

            await download;

            if (!ct.IsCancellationRequested)
            {
                blog.LastCompleteCrawl = DateTime.Now;
            }

            blog.Save();

            UpdateProgressQueueInformation("");
        }

        private async Task GetUrlsAsync()
        {
            var semaphoreSlim = new SemaphoreSlim(shellService.Settings.ConcurrentScans);
            var trackedTasks = new List<Task>();

            if (!await CheckIfLoggedIn())
            {
                Logger.Error("TumblrLikedByCrawler:GetUrlsAsync: {0}", "User not logged in");
                shellService.ShowError(new Exception("User not logged in"), Resources.NotLoggedIn, blog.Name);
                producerConsumerCollection.CompleteAdding();
                return;
            }

            long pagination = CreateStartPagination();

            // TODO: find way to parallelize without losing content.
            foreach (int crawlerNumber in Enumerable.Range(0, 1))
            {
                await semaphoreSlim.WaitAsync();

                trackedTasks.Add(new Func<Task>(async () =>
                {
                    try
                    {
                        await AddUrlsToDownloadList(pagination, crawlerNumber);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        semaphoreSlim.Release();
                    }
                })());
            }
            await Task.WhenAll(trackedTasks);

            producerConsumerCollection.CompleteAdding();

            //if (!ct.IsCancellationRequested)
            //{
                UpdateBlogStats();
            //}
        }

        private long CreateStartPagination()
        {
            long pagination = DateTimeOffset.Now.ToUnixTimeSeconds();
            if (!string.IsNullOrEmpty(blog.DownloadTo))
            {
                var downloadTo = DateTime.ParseExact(blog.DownloadTo, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None);
                var dateTimeOffset = new DateTimeOffset(downloadTo);
                pagination = dateTimeOffset.ToUnixTimeSeconds();
            }
            return pagination;
        }

        private async Task<bool> CheckIfLoggedIn()
        {
            string document = await RequestDataAsync(blog.Url + "/page/1", "https://www.tumblr.com/", "https://" + blog.Name.Replace("+", "-") + ".tumblr.com");
            return !document.Contains("<div class=\"signup_view account login\"");
        }

        private async Task AddUrlsToDownloadList(long pagination, int crawlerNumber)
        {
            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }
                if (pt.IsPaused)
                {
                    pt.WaitWhilePausedWithResponseAsyc().Wait();
                }

                string document = await RequestDataAsync(blog.Url + "/page/" + crawlerNumber + "/" + pagination, "https://www.tumblr.com/", "https://" + blog.Name.Replace("+", "-") + ".tumblr.com");
                if (document.Contains("<div class=\"no_posts_found\""))
                {
                    return;
                }

                try
                {
                    AddPhotoUrlToDownloadList(document);
                    AddVideoUrlToDownloadList(document);
                }
                catch (NullReferenceException)
                {
                }

                Interlocked.Increment(ref numberOfPagesCrawled);
                UpdateProgressQueueInformation(Resources.ProgressGetUrlShort, numberOfPagesCrawled);
                pagination = ExtractNextPageLink(document);
                crawlerNumber++;
                if (!CheckIfWithinTimespan(pagination))
                    return;
            }
        }

        private long ExtractNextPageLink(string document)
        {
            // Example pagination:
            //
            // <div id="pagination" class="pagination "><a id="previous_page_link" href="/liked/by/wallpaperfx/page/3/-1457140452" class="previous button chrome">Previous</a>
            // <a id="next_page_link" href="/liked/by/wallpaperfx/page/5/1457139681" class="next button chrome blue">Next</a></div></div>
            long unixTime = 0;
            //int pageNumber = 1;
            var pagination = "(id=\"next_page_link\" href=\"[A-Za-z0-9_/:.]+/([0-9]+)/([A-Za-z0-9]+))\"";
            long.TryParse(Regex.Match(document, pagination).Groups[3].Value, out unixTime);
            //int.TryParse(Regex.Match(document, pagination).Groups[2].Value, out pageNumber);
            return unixTime;
        }

        private bool CheckIfWithinTimespan(long pagination)
        {
            if (!string.IsNullOrEmpty(blog.DownloadFrom))
            {
                var downloadFrom = DateTime.ParseExact(blog.DownloadFrom, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None);
                var dateTimeOffset = new DateTimeOffset(downloadFrom);
                if (pagination < dateTimeOffset.ToUnixTimeSeconds())
                    return false;
            }
            return true;
        }

        private void AddPhotoUrlToDownloadList(string document)
        {
            if (blog.DownloadPhoto)
            {
                var regex = new Regex("src=\"(http[A-Za-z0-9_/:.]*media.tumblr.com[A-Za-z0-9_/:.]*(jpg|png|gif))\"");
                foreach (Match match in regex.Matches(document))
                {
                    string imageUrl = match.Groups[1].Value;
                    if (imageUrl.Contains("avatar") || imageUrl.Contains("previews"))
                        continue;                    
                    if (blog.SkipGif && imageUrl.EndsWith(".gif"))
                    {
                        continue;
                    }
                    imageUrl = ResizeTumblrImageUrl(imageUrl);
                    // TODO: add valid postID
                    AddToDownloadList(new TumblrPost(PostTypes.Photo, imageUrl, Guid.NewGuid().ToString("N")));
                }
            }
        }

        private void AddVideoUrlToDownloadList(string document)
        {
            if (blog.DownloadVideo)
            {
                var regex = new Regex("src=\"(http[A-Za-z0-9_/:.]*.com/video_file/[A-Za-z0-9_/:.]*)\"");
                foreach (Match match in regex.Matches(document))
                {
                    string videoUrl = match.Groups[1].Value;
                    // TODO: add valid postID
                    if (shellService.Settings.VideoSize == 1080)
                    {
                        // TODO: add valid postID
                        AddToDownloadList(new TumblrPost(PostTypes.Video, videoUrl.Replace("/480", "") + ".mp4", Guid.NewGuid().ToString("N")));
                    }
                    else if (shellService.Settings.VideoSize == 480)
                    {
                        // TODO: add valid postID
                        AddToDownloadList(new TumblrPost(PostTypes.Video, 
                            "https://vt.tumblr.com/" + videoUrl.Replace("/480", "").Split('/').Last() + "_480.mp4",
                            Guid.NewGuid().ToString("N")));
                    }
                }
            }
        }
    }
}
