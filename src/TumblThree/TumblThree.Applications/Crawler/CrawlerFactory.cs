﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;

using TumblThree.Applications.DataModels;
using TumblThree.Applications.Downloader;
using TumblThree.Applications.Properties;
using TumblThree.Applications.Services;
using TumblThree.Domain.Models;

namespace TumblThree.Applications.Crawler
{
    [Export(typeof(ICrawlerFactory))]
    public class CrawlerFactory : ICrawlerFactory
    {
        private readonly AppSettings settings;
        private readonly ISharedCookieService cookieService;

        [ImportingConstructor]
        internal CrawlerFactory(ShellService shellService, ISharedCookieService cookieService)
        {
            this.settings = shellService.Settings;
            this.cookieService = cookieService;
        }

        [ImportMany(typeof(ICrawler))]
        private IEnumerable<Lazy<ICrawler, ICrawlerData>> DownloaderFactoryLazy { get; set; }

        public ICrawler GetCrawler(BlogTypes blogtype)
        {
            Lazy<ICrawler, ICrawlerData> downloader =
                DownloaderFactoryLazy.FirstOrDefault(list => list.Metadata.BlogType == blogtype);

            if (downloader != null)
            {
                return downloader.Value;
            }
            throw new ArgumentException("Website is not supported!", "blogType");
        }

        public ICrawler GetCrawler(BlogTypes blogtype, CancellationToken ct, PauseToken pt, IProgress<DownloadProgress> progress, IShellService shellService, ICrawlerService crawlerService, IBlog blog)
        {
            BlockingCollection<TumblrPost> producerConsumerCollection = GetProducerConsumerCollection();
            IFiles files = LoadFiles(blog);
            IWebRequestFactory webRequestFactory = GetWebRequestFactory();
            IGfycatParser gfycatParser = GetGfycatParser(webRequestFactory, ct);
            switch (blogtype)
            {
                case BlogTypes.tumblr:
                    return new TumblrBlogCrawler(shellService, ct, pt, progress, crawlerService, webRequestFactory, cookieService, GetTumblrDownloader(ct, pt, progress, shellService, crawlerService, blog, files, producerConsumerCollection), GetImgurParser(), gfycatParser, GetWebmshareParser(), producerConsumerCollection, blog);
                case BlogTypes.tmblrpriv:
                    return new TumblrPrivateCrawler(shellService, ct, pt, progress, crawlerService, webRequestFactory ,cookieService, GetTumblrDownloader(ct, pt, progress, shellService, crawlerService, blog, files, producerConsumerCollection), GetImgurParser(), gfycatParser, GetWebmshareParser(), producerConsumerCollection, blog);
                case BlogTypes.tlb:
                    return new TumblrLikedByCrawler(shellService, ct, pt, progress, crawlerService, webRequestFactory, cookieService, GetTumblrDownloader(ct, pt, progress, shellService, crawlerService, blog, files, producerConsumerCollection), producerConsumerCollection, blog);
                case BlogTypes.tumblrsearch:
                    return new TumblrSearchCrawler(shellService, ct, pt, progress, crawlerService, webRequestFactory, cookieService, GetTumblrDownloader(ct, pt, progress, shellService, crawlerService, blog, files, producerConsumerCollection), producerConsumerCollection, blog);
                case BlogTypes.tumblrtagsearch:
                    return new TumblrTagSearchCrawler(shellService, ct, pt, progress, crawlerService, webRequestFactory, cookieService, GetTumblrDownloader(ct, pt, progress, shellService, crawlerService, blog, files, producerConsumerCollection), producerConsumerCollection, blog);
                default:
                    throw new ArgumentException("Website is not supported!", "blogType");
            }
        }

        private IFiles LoadFiles(IBlog blog)
        {
            return new Files().Load(blog.ChildId);
        }

        private IWebRequestFactory GetWebRequestFactory()
        {
            return new WebRequestFactory(settings, cookieService);
        }
        private IImgurParser GetImgurParser()
        {
            return new ImgurParser();
        }

        private IGfycatParser GetGfycatParser(IWebRequestFactory webRequestFactory, CancellationToken ct)
        {
            return new GfycatParser(settings, webRequestFactory, ct);
        }

        private IWebmshareParser GetWebmshareParser()
        {
            return new WebmshareParser();
        }

        private FileDownloader GetFileDownloader(CancellationToken ct)
        {
            return new FileDownloader(settings, ct, cookieService);
        }

        private static IBlogService GetBlogService(IBlog blog, IFiles files)
        {
            return new BlogService(blog, files);
        }

        private TumblrDownloader GetTumblrDownloader(CancellationToken ct, PauseToken pt, IProgress<DownloadProgress> progress, IShellService shellService, ICrawlerService crawlerService, IBlog blog, IFiles files, BlockingCollection<TumblrPost> producerConsumerCollection)
        {
            return new TumblrDownloader(shellService, ct, pt, progress, producerConsumerCollection, GetFileDownloader(ct), crawlerService, blog, files);
        }

        private BlockingCollection<TumblrPost> GetProducerConsumerCollection()
        {
            return new BlockingCollection<TumblrPost>();
        }
    }
}
