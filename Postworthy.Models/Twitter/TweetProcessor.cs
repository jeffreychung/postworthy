﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Postworthy.Models;
using System.Threading.Tasks;
using LinqToTwitter;
using System.Threading;
using System.IO;
using Postworthy.Models.Core;
using System.Configuration;
using Postworthy.Models.Account;
using System.Drawing;

namespace Postworthy.Models.Twitter
{
    public class TweetProcessor
    {
        private const string YOUTUBE_EMBED = "https://www.youtube.com/embed/";
        private List<KeyValuePair<string, Action>> UriActions { get; set; }

        private TaskFactory TaskFactory { get; set; }

        public TweetProcessor(IEnumerable<Tweet> tweets, int RetweetThreshold = 0, bool force = false)
        {
            //int RetweetThreshold = UsersCollection.Single(screenname).RetweetThreshold;

            UriActions = new List<KeyValuePair<string, Action>>();

            TaskFactory = new TaskFactory(new CustomTaskScheduler(15));

            foreach (var t in tweets)
            {
                if ((t.RetweetCount > RetweetThreshold && t.Links.Count == 0) || force) 
                    ExtractUriTasks(t);
            }
        }

        public void Start()
        {
            var groups = UriActions
                .GroupBy(x => x.Key); //Group By Domain

            var actionChains = groups
                .Select(g => ExecuteActionChain(g.Select(x => x.Value), new Task(() => { /* Completion Task */ }))) //Execute one at a time per domain
                .ToArray();

            Task.WaitAll(actionChains);
        }

        private Task ExecuteActionChain(IEnumerable<Action> actions, Task completion)
        {
            var action = actions.FirstOrDefault();
            
            if (action != null)
            {
                var task = TaskFactory.StartNew(action);
                task.ContinueWith(t =>
                {
                    Thread.Sleep(1000);
                    ExecuteActionChain(actions.Skip(1), completion);
                });
            }
            else completion.Start();

            return completion;
        }

        private void ExtractUriTasks(Tweet tweet)
        {
            if (tweet.Status.Entities != null && tweet.Status.Entities.UrlEntities != null)
            {
                foreach (var urlmentions in tweet.Status.Entities.UrlEntities)
                {
                    var link = new UriEx(urlmentions.ExpandedUrl);
                    UriActions.Add(new KeyValuePair<string, Action>(link.Uri.Authority, () => CreateUriAction(link, () => ProcessingComplete(tweet, link, urlmentions))));
                    tweet.Links.Add(link);
                    tweet.TweetText = tweet.Status.Text.Replace(urlmentions.Url, "<a target=\"_blank\" href=\"" + urlmentions.ExpandedUrl + "\">[" + link.Title + "]</a>");
                }
            }
            if (tweet.Status.Entities != null && tweet.Status.Entities.MediaEntities != null)
            {
                foreach (var media in tweet.Status.Entities.MediaEntities)
                {
                    var link = new UriEx(media.ExpandedUrl);
                    UriActions.Add(new KeyValuePair<string, Action>(link.Uri.Authority, () => CreateUriAction(link, () => ProcessingComplete(tweet, link, media))));
                    tweet.Links.Add(link);
                    tweet.TweetText = tweet.Status.Text.Replace(media.Url, "<a target=\"_blank\" href=\"" + media.ExpandedUrl + "\">[" + link.Title + "]</a>");
                }
            }
            /*
            if (Links.Count == 0 && !string.IsNullOrEmpty(Status.Text))
            {
                var regx = new Regex("http://([\\w+?\\.\\w+])+([a-zA-Z0-9\\~\\!\\@\\#\\$\\%\\^\\&amp;\\*\\(\\)_\\-\\=\\+\\\\\\/\\?\\.\\:\\;\\'\\,]*)?", RegexOptions.IgnoreCase);
                var matches = regx.Matches(Status.Text);
                for (int i = 0; i < matches.Count; i++ )
                {
                    var m = matches[i];
                    var link = new UriEx(m.Value);
                    CompletionTasks.Add(link.CreateProcessUriTask(l => 
                        {
                            TweetText = Status.Text.Replace(m.Value, "<a target=\"_blank\" href=\"" + m.Value + "\">[" + l.Title + "]</a>");
                        }));
                    link.PropertyChanged += new System.ComponentModel.PropertyChangedEventHandler((x, y) => { base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Links")); });
                    Links.Add(link);
                    TweetText = Status.Text.Replace(m.Value, "<a target=\"_blank\" href=\"" + m.Value + "\">[" + link.Title + "]</a>");
                }
            }
             */
        }

        private void ProcessingComplete(Tweet tweet, UriEx l, object m)
        {
            if (m is UrlEntity || m is MediaEntity)
            {
                tweet.TweetText = tweet.Status.Text.Replace(((dynamic)m).Url, "<a target=\"_blank\" href=\"" + l.Uri + "\">[" + l.Title + "]</a>");
            }

            if (l.Image != null && tweet.Image == null)
            {
                try
                {
                    using (var response = l.Image.GetWebRequest().GetResponse())
                    {
                        using (var responseStream = response.GetResponseStream())
                        {
                            if (responseStream != null && responseStream.CanRead)
                            {
                                var imgData = new byte[responseStream.Length];
                                responseStream.Read(imgData, 0, imgData.Length);
                                using (var memstrm = new MemoryStream(imgData))
                                {
                                    tweet.Image = (Bitmap)Bitmap.FromStream(memstrm);
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }

        public void CreateUriAction(UriEx uriex, Action Finished)
        {
            uriex.Init();

            uriex.UrlTweetCount = uriex.Uri.GetTweetCount();
            uriex.UrlFacebookShareCount = uriex.Uri.GetFacebookShareCount();

            if (uriex.IsHtmlContentUrl)
            {
                var doc = new HtmlAgilityPack.HtmlDocument();
                try
                {
                    var req = uriex.Uri.GetWebRequest();
                    using (var resp = req.GetResponse())
                    {
                        using (var reader = new StreamReader(resp.GetResponseStream(), true))
                        {
                            doc.Load(reader);
                        }
                    }
                }
                catch(Exception ex)
                {
                    ex = ex;
                }
                if (doc.DocumentNode != null)
                {
                    var nodes = doc.DocumentNode.SelectNodes("//title");
                    if (nodes != null && nodes.Count > 0)
                    {
                        uriex.Title = nodes.First().InnerText.Trim();
                    }

                    nodes = doc.DocumentNode.SelectNodes("//link");
                    if (nodes != null && nodes.Count > 0)
                    {
                        var ogMeta = nodes
                            .Where(m => m.Attributes.SingleOrDefault(a => a.Name.ToLower() == "rel" && a.Value.ToLower().StartsWith("image_src")) != null)
                            .Select(m =>
                            new
                            {
                                Property = m.Attributes["rel"].Value.ToLower(),
                                Content = m.Attributes["href"].Value
                            });
                        if (ogMeta != null && ogMeta.Count() > 0)
                        {
                            uriex.Image = ogMeta.Where(x => x.Property == "image_src").Select(x => CreateUriSafely(uriex.Uri, x.Content)).FirstOrDefault();
                        }
                    }

                    nodes = doc.DocumentNode.SelectNodes("//meta");
                    if (nodes != null && nodes.Count > 0)
                    {
                        var ogMeta = nodes
                            .Where(m => m.Attributes.SingleOrDefault(a => a.Name.ToLower() == "property" && a.Value.ToLower().StartsWith("og:")) != null)
                            .Select(m =>
                            new
                            {
                                Property = m.Attributes["property"].Value.ToLower(),
                                Content = m.Attributes["content"] != null ? m.Attributes["content"].Value : (m.Attributes["value"] != null ? m.Attributes["value"].Value : "")
                            });
                        if (ogMeta != null && ogMeta.Count() > 0)
                        {
                            uriex.Title = (ogMeta.Where(x => x.Property == "og:title" && !string.IsNullOrEmpty(x.Content)).Select(x => x.Content).FirstOrDefault() ?? "").Trim();
                            uriex.Description = ogMeta.Where(x => x.Property == "og:description" && !string.IsNullOrEmpty(x.Content)).Select(x => x.Content).FirstOrDefault() ?? "";
                            uriex.Image = ogMeta.Where(x => x.Property == "og:image" && !string.IsNullOrEmpty(x.Content)).Select(x => CreateUriSafely(uriex.Uri, x.Content)).FirstOrDefault();
                            uriex.Video = ogMeta.Where(x => x.Property == "og:video" && !string.IsNullOrEmpty(x.Content)).Select(x => CreateUriSafely(uriex.Uri, x.Content)).FirstOrDefault();
                            uriex.Video = CleanYouTube(uriex.Video);
                        }

                        var twitterMeta = nodes
                            .Where(m => m.Attributes.SingleOrDefault(a => a.Name.ToLower() == "property" && a.Value.ToLower().StartsWith("twitter:")) != null)
                            .Select(m =>
                            new
                            {
                                Property = m.Attributes["property"].Value.ToLower(),
                                Content = m.Attributes["content"] != null ? m.Attributes["content"].Value : (m.Attributes["value"] != null ? m.Attributes["value"].Value : "")
                            });
                        if (twitterMeta != null && twitterMeta.Count() > 0)
                        {
                            if(string.IsNullOrEmpty(uriex.Title))
                                uriex.Title = (twitterMeta.Where(x => x.Property == "twitter:title" && !string.IsNullOrEmpty(x.Content)).Select(x => x.Content).FirstOrDefault() ?? "").Trim();
                            if (string.IsNullOrEmpty(uriex.Description))
                                uriex.Description = twitterMeta.Where(x => x.Property == "twitter:description" && !string.IsNullOrEmpty(x.Content)).Select(x => x.Content).FirstOrDefault() ?? "";
                            if (uriex.Image == null)
                                uriex.Image = twitterMeta.Where(x => x.Property == "twitter:image" && !string.IsNullOrEmpty(x.Content)).Select(x => CreateUriSafely(uriex.Uri, x.Content)).FirstOrDefault();
                            if (uriex.Video == null)
                            {
                                uriex.Video = twitterMeta.Where(x => x.Property == "twitter:player" && !string.IsNullOrEmpty(x.Content)).Select(x => CreateUriSafely(uriex.Uri, x.Content)).FirstOrDefault();
                                uriex.Video = CleanYouTube(uriex.Video);
                            }
                        }
                    }

                    if(uriex.Video == null)
                    {
                        nodes = doc.DocumentNode.SelectNodes("//iframe");
                        if (nodes != null && nodes.Count > 0)
                        {
                            var iframes = nodes
                            .Where(i => i.Attributes["src"] != null && i.Attributes["src"].Value.ToLower().StartsWith(YOUTUBE_EMBED))
                            .Select(i => i.Attributes["src"].Value);

                            if (iframes.Count() > 0)
                                uriex.Video = new Uri(iframes.FirstOrDefault());
                        }
                    }
                }
            }
            else if(uriex.IsImageContentUrl)
            {
                    uriex.Image = uriex.Uri;
                    uriex.Title = uriex.Uri.ToString();
            }

            Finished();
        }

        private Uri CreateUriSafely(Uri uri, string content)
        {
            var baseUri = uri.GetLeftPart(UriPartial.Authority);
            var dirUri = string.Join("/", baseUri.Split(new string[] { "/" }, StringSplitOptions.RemoveEmptyEntries).Reverse().Skip(1).Reverse());

            return content.StartsWith("http") ?
                new Uri(content.Trim()) :
                content.StartsWith("/") ?
                    new Uri(baseUri + content.Trim()) :
                    //This may look a bit strange but the split join above could leave you with google.com if the full url is http(s)://google.com
                    content.StartsWith("../") && dirUri.Contains("://") ?
                        new Uri(dirUri + content.Trim().Replace("..","")) :
                        null;
        }

        private Uri CleanYouTube(Uri Video)
        {
            if (Video != null)
            {
                string uri = Video.ToString().ToLower();
                if (uri.Contains("youtube.com") && !uri.Contains(YOUTUBE_EMBED))
                {
                    string code = Video.ToString().Split(new string[] { "/v/" }, StringSplitOptions.RemoveEmptyEntries)[1].Split('?')[0];
                    return new Uri(YOUTUBE_EMBED + code);
                }
            }
            return Video;
        }
    }
}
